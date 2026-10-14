using System.Diagnostics;
using NexScreen.Core.Display;
using NexScreen.Core.Input;
using NexScreen.Core.Protocol;
using NexScreen.Core.Video;

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
        var refresh = hello.RefreshRate > 0 ? $" @ {hello.RefreshRate} Hz" : "";
        Log.Info($"Cliente: {hello.Device} {hello.Width}x{hello.Height}{refresh}, {hello.Dpi} dpi");

        // El dispositivo se usa apaisado: el lado largo es el ancho. H.264 necesita lados pares.
        var maxWidth = Math.Max(hello.Width, hello.Height) & ~1;
        var maxHeight = Math.Min(hello.Width, hello.Height) & ~1;
        var fps = StreamTuning.ChooseFps(_options.Fps, hello.RefreshRate);
        var bitrate = _options.ScaleBitrate
            ? StreamTuning.ScaleBitrate(_options.BitrateMbps, maxWidth, maxHeight, fps)
            : _options.BitrateMbps;
        await channel.WriteJsonAsync(
            MessageType.Config,
            new ConfigMessage(Wire.Version, "h264", maxWidth, maxHeight, fps),
            ct).ConfigureAwait(false);

        _observer.OnClientConnected(hello.Device, maxWidth, maxHeight);

        var extend = _options.Mode == ScreenMode.Extend;
        Func<CaptureTarget> resolveTarget = () => DxgiOutputs.ByIndex(_options.Monitor);
        if (extend)
        {
            // El monitor virtual toma la resolución y la frecuencia nativas del dispositivo: no hay escalado.
            var gdiName = await VirtualDisplay.AttachAsync(maxWidth, maxHeight, fps, _options.Position, ct)
                .ConfigureAwait(false);
            // El índice DXGI puede cambiar si el usuario reordena las pantallas durante la sesión.
            resolveTarget = () => VirtualDisplay.CaptureTarget(gdiName);
            // Antes de empezar a capturar: cambiar la escala reconfigura la pantalla.
            var scale = _options.AutoScale ? StreamTuning.AutoScalePercent(hello.Dpi) : _options.ScalePercent;
            if (scale is { } percent) ApplyScale(percent);
        }

        try
        {
            await StreamAsync(channel, resolveTarget, new StreamFormat(maxWidth, maxHeight, fps, bitrate), ct)
                .ConfigureAwait(false);
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
            // Windows limita la escala según la resolución (por ejemplo, 225 % en 3120×1440).
            Log.Info(applied == percent
                ? $"Escala del dispositivo: {applied} %"
                : $"Escala del dispositivo: {applied} % (se pidió {percent} %; es lo más cercano que permite Windows)");
        }
        catch (InvalidOperationException ex)
        {
            // Sin escala el dispositivo funciona igual (texto más chico): no cortar la sesión.
            Log.Warn($"No se pudo ajustar la escala del dispositivo: {ex.Message}");
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

    private async Task StreamAsync(MessageChannel channel, Func<CaptureTarget> resolveTarget, StreamFormat format, CancellationToken ct)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var injector = new PointerInjector(CachedScreen(resolveTarget));
        var streamer = new VideoStreamer(_options, _observer, channel, resolveTarget, format);
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
    private static Func<ScreenRect> CachedScreen(Func<CaptureTarget> resolveTarget)
    {
        ScreenRect? cached = null;
        var age = Stopwatch.StartNew();
        return () =>
        {
            if (cached is null || age.Elapsed > ScreenCacheTtl)
            {
                cached = DxgiOutputs.Bounds(resolveTarget());
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
