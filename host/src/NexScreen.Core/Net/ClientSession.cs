using System.Diagnostics;
using NexScreen.Core.Display;
using NexScreen.Core.Input;
using NexScreen.Core.Protocol;

namespace NexScreen.Core.Net;

/// <summary>Una conexión con un cliente: configuración, monitor virtual, input y keep-alive (el video va en <see cref="VideoStreamer"/>).</summary>
internal sealed class ClientSession
{
    private static readonly TimeSpan ScreenCacheTtl = TimeSpan.FromSeconds(1);
    private const int MaxInputErrorsLogged = 5;

    private readonly HostOptions _options;
    private readonly IStreamObserver _observer;
    private int _inputErrorsLogged;

    public ClientSession(HostOptions options, IStreamObserver observer)
    {
        _options = options;
        _observer = observer;
    }

    /// <summary>Corre la sesión sobre un canal que ya pasó el <see cref="Handshake"/>.</summary>
    public async Task RunAsync(MessageChannel channel, HelloMessage hello, CancellationToken ct)
    {
        Log.Info($"Cliente: {hello.Device} {hello.Width}x{hello.Height} @ {hello.Dpi} dpi");

        // La tablet se usa apaisada: el lado largo es el ancho.
        var maxWidth = Math.Max(hello.Width, hello.Height);
        var maxHeight = Math.Min(hello.Width, hello.Height);
        await channel.WriteJsonAsync(
            MessageType.Config,
            new ConfigMessage(Wire.Version, "h264", maxWidth, maxHeight, _options.Fps),
            ct).ConfigureAwait(false);

        _observer.OnClientConnected(hello.Device, maxWidth, maxHeight);

        var extend = _options.Mode == ScreenMode.Extend;
        Func<int> resolveMonitor = () => _options.Monitor;
        if (extend)
        {
            // El monitor virtual toma la resolución nativa de la tablet: no hay escalado.
            var gdiName = await VirtualDisplay.AttachAsync(maxWidth, maxHeight, _options.Fps, _options.Position, ct)
                .ConfigureAwait(false);
            // El índice DXGI puede cambiar si el usuario reordena las pantallas durante la sesión.
            resolveMonitor = () => VirtualDisplay.OutputIndex(gdiName);
            // Antes de empezar a capturar: cambiar la escala reconfigura la pantalla.
            if (_options.ScalePercent is { } scale) ApplyScale(scale);
        }

        try
        {
            await StreamAsync(channel, resolveMonitor, maxWidth, maxHeight, ct).ConfigureAwait(false);
        }
        finally
        {
            if (extend) DetachVirtualDisplay();
        }
    }

    private static void ApplyScale(int percent)
    {
        try
        {
            var applied = DisplayConfig.SetVirtualScale(percent);
            Log.Info($"Escala de la tablet: {applied} %");
        }
        catch (InvalidOperationException ex)
        {
            // Sin escala la tablet funciona igual (texto más chico): no cortar la sesión.
            Log.Warn($"No se pudo ajustar la escala de la tablet: {ex.Message}");
        }
    }

    private static void DetachVirtualDisplay()
    {
        try
        {
            VirtualDisplay.Detach(rememberPosition: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"No se pudo quitar el monitor virtual: {ex.Message}");
        }
    }

    private async Task StreamAsync(MessageChannel channel, Func<int> resolveMonitor, int maxWidth, int maxHeight, CancellationToken ct)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var injector = new PointerInjector(CachedScreen(resolveMonitor));
        var streamer = new VideoStreamer(_options, _observer, channel, resolveMonitor, maxWidth, maxHeight);
        var receive = ReceiveLoopAsync(channel, injector, streamer, sessionCts.Token);
        var video = streamer.RunAsync(sessionCts.Token);

        var finished = await Task.WhenAny(receive, video).ConfigureAwait(false);
        await sessionCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(receive, video).ConfigureAwait(false);
        }
        catch
        {
            // La otra tarea falla al cancelarse o al cerrarse el socket; lo relevante es la primera.
        }

        await finished.ConfigureAwait(false);
    }

    /// <summary>
    /// Rectángulo del monitor capturado para mapear el input. Se cachea un momento porque llegan
    /// cientos de eventos por segundo, y se refresca por si el usuario mueve el monitor.
    /// </summary>
    private static Func<ScreenRect> CachedScreen(Func<int> resolveMonitor)
    {
        ScreenRect? cached = null;
        var age = Stopwatch.StartNew();
        return () =>
        {
            if (cached is null || age.Elapsed > ScreenCacheTtl)
            {
                cached = DxgiOutputs.Bounds(resolveMonitor());
                age.Restart();
            }

            return cached.Value;
        };
    }

    private async Task ReceiveLoopAsync(
        MessageChannel channel, PointerInjector injector, VideoStreamer streamer, CancellationToken ct)
    {
        while (await channel.ReadAsync(ct).ConfigureAwait(false) is { } message)
        {
            switch (message.Type)
            {
                case MessageType.Ping:
                    await channel.WriteAsync(MessageType.Pong, message.Payload, ct).ConfigureAwait(false);
                    break;
                case MessageType.Pointer:
                    InjectPointer(injector, message.Payload);
                    break;
                case MessageType.RequestKeyframe:
                    streamer.RequestKeyframe();
                    break;
                case MessageType.Pong:
                    break;
                default:
                    Log.Debug($"Mensaje ignorado: {message.Type}");
                    break;
            }
        }

        Log.Info("El cliente cerró la conexión");
    }

    /// <summary>Un error de input no debe cortar el video: se registra (con límite) y se sigue.</summary>
    private void InjectPointer(PointerInjector injector, byte[] payload)
    {
        try
        {
            injector.Inject(PointerMessage.Parse(payload));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_inputErrorsLogged++ < MaxInputErrorsLogged) Log.Warn($"Input descartado: {ex.Message}");
        }
    }
}
