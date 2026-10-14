using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using NexScreen.Core.Display;
using NexScreen.Core.Input;
using NexScreen.Core.Protocol;
using NexScreen.Core.Video;

namespace NexScreen.Core.Net;

/// <summary>Una conexión con un cliente: handshake, streaming de video y keep-alive.</summary>
internal sealed class ClientSession
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(30);
    private const int MaxRestartsPerWindow = 10;
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

    public async Task RunAsync(TcpClient client, CancellationToken ct)
    {
        await using var channel = new MessageChannel(client.GetStream());
        var hello = await ReadHelloAsync(channel, ct).ConfigureAwait(false);
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
        var receive = ReceiveLoopAsync(channel, injector, sessionCts.Token);
        var video = StreamVideoAsync(channel, resolveMonitor, maxWidth, maxHeight, sessionCts.Token);

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

    private static async Task<HelloMessage> ReadHelloAsync(MessageChannel channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);

        var message = await channel.ReadAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("El cliente cerró la conexión antes del Hello");
        if (message.Type != MessageType.Hello) throw new InvalidDataException($"Se esperaba Hello y llegó {message.Type}");

        var hello = JsonSerializer.Deserialize<HelloMessage>(message.Payload, Wire.Json)
            ?? throw new InvalidDataException("Hello vacío");
        if (hello.Protocol != Wire.Version)
        {
            throw new InvalidDataException($"Versión de protocolo incompatible: cliente {hello.Protocol}, host {Wire.Version}");
        }
        if (hello.Width <= 0 || hello.Height <= 0) throw new InvalidDataException("Resolución de cliente inválida");

        return hello;
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

    private async Task ReceiveLoopAsync(MessageChannel channel, PointerInjector injector, CancellationToken ct)
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

    /// <summary>
    /// Mantiene la captura viva. Si nunca produjo video prueba el siguiente encoder; si se cortó
    /// después de transmitir (Windows invalida Desktop Duplication al reordenar pantallas, cambiar
    /// la resolución, mostrar UAC o bloquear la sesión) la reinicia sin cortar la conexión.
    /// </summary>
    private async Task StreamVideoAsync(MessageChannel channel, Func<int> resolveMonitor, int maxWidth, int maxHeight, CancellationToken ct)
    {
        EncoderKind[] encoders = _options.Encoder == EncoderKind.Auto
            ? [EncoderKind.Qsv, EncoderKind.X264]
            : [_options.Encoder];

        var encoderIndex = 0;
        var hasStreamed = false;
        var recentRestarts = new Queue<DateTime>();
        while (true)
        {
            var settings = new VideoSourceSettings(
                resolveMonitor(), _options.Fps, _options.BitrateMbps, maxWidth, maxHeight, encoders[encoderIndex]);

            long framesSent;
            string diagnostics, sourceName;
            await using (var source = FfmpegVideoSource.Start(_options.FfmpegPath, settings))
            {
                sourceName = source.Name;
                if (!hasStreamed)
                {
                    Log.Info($"Encoder: {source.Name}, máx {maxWidth}x{maxHeight} @ {_options.Fps} fps, {_options.BitrateMbps} Mbps");
                }

                framesSent = await PumpAsync(source, channel, _observer, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                diagnostics = source.Diagnostics;
            }

            if (!hasStreamed && framesSent == 0)
            {
                if (encoderIndex == encoders.Length - 1)
                {
                    throw new IOException($"El encoder {sourceName} no produjo video. {diagnostics}");
                }

                Log.Warn($"{sourceName} no produjo video, probando el siguiente encoder. {diagnostics}");
                encoderIndex++;
                continue;
            }

            hasStreamed = true;
            var now = DateTime.UtcNow;
            while (recentRestarts.Count > 0 && now - recentRestarts.Peek() > RestartWindow) recentRestarts.Dequeue();
            if (recentRestarts.Count >= MaxRestartsPerWindow)
            {
                throw new IOException($"La captura falló {MaxRestartsPerWindow} veces seguidas. {diagnostics}");
            }

            recentRestarts.Enqueue(now);
            Log.Warn($"La captura se interrumpió ({FirstLine(diagnostics)}); reiniciando");
            await Task.Delay(RestartDelay, ct).ConfigureAwait(false);
            // La causa más común es que el usuario movió el monitor: guardar dónde lo dejó.
            if (_options.Mode == ScreenMode.Extend) TryRememberPosition();
        }
    }

    private static void TryRememberPosition()
    {
        try
        {
            VirtualDisplay.RememberPosition();
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"No se pudo guardar la posición del monitor virtual: {ex.Message}");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "sin detalle";

    /// <summary>Envía frames hasta que la fuente termina. Devuelve la cantidad enviada.</summary>
    private static async Task<long> PumpAsync(
        IVideoSource source, MessageChannel channel, IStreamObserver observer, CancellationToken ct)
    {
        using var firstFrameTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstFrameTimeout.CancelAfter(FirstFrameTimeout);

        long frames = 0, bytes = 0, intervalFrames = 0, intervalBytes = 0;
        var interval = Stopwatch.StartNew();
        // Si vence el timeout la fuente mata ffmpeg y el enumerador termina sin frames.
        await foreach (var frame in source.ReadFramesAsync(firstFrameTimeout.Token).ConfigureAwait(false))
        {
            if (frames == 0) firstFrameTimeout.CancelAfter(Timeout.InfiniteTimeSpan);

            await channel.WriteVideoAsync(frame, ct).ConfigureAwait(false);
            frames++;
            bytes += frame.Data.Length;
            if ((frame.Flags & FrameFlags.Config) == 0) intervalFrames++;
            intervalBytes += frame.Data.Length;

            if (interval.Elapsed >= StatsInterval)
            {
                var seconds = interval.Elapsed.TotalSeconds;
                var fps = intervalFrames / seconds;
                var mbps = intervalBytes * 8 / seconds / 1_000_000;
                Log.Info($"Stream: {fps:F1} fps, {mbps:F1} Mbps");
                observer.OnStats(fps, mbps);
                intervalFrames = intervalBytes = 0;
                interval.Restart();
            }
        }

        if (frames == 0 && firstFrameTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Log.Warn($"{source.Name} no entregó el primer frame en {FirstFrameTimeout.TotalSeconds:F0} s");
        }

        Log.Debug($"Fuente terminada: {frames} frames, {bytes / 1024} KiB");
        return frames;
    }
}
