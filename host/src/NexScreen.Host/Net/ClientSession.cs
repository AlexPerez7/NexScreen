using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using NexScreen.Host.Protocol;
using NexScreen.Host.Video;

namespace NexScreen.Host.Net;

/// <summary>Una conexión con un cliente: handshake, streaming de video y keep-alive.</summary>
internal sealed class ClientSession
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);

    private readonly HostOptions _options;

    public ClientSession(HostOptions options) => _options = options;

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

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receive = ReceiveLoopAsync(channel, sessionCts.Token);
        var video = StreamVideoAsync(channel, maxWidth, maxHeight, sessionCts.Token);

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

    private static async Task ReceiveLoopAsync(MessageChannel channel, CancellationToken ct)
    {
        while (await channel.ReadAsync(ct).ConfigureAwait(false) is { } message)
        {
            switch (message.Type)
            {
                case MessageType.Ping:
                    await channel.WriteAsync(MessageType.Pong, message.Payload, ct).ConfigureAwait(false);
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

    private async Task StreamVideoAsync(MessageChannel channel, int maxWidth, int maxHeight, CancellationToken ct)
    {
        EncoderKind[] encoders = _options.Encoder == EncoderKind.Auto
            ? [EncoderKind.Qsv, EncoderKind.X264]
            : [_options.Encoder];

        for (var i = 0; i < encoders.Length; i++)
        {
            var settings = new VideoSourceSettings(
                _options.Monitor, _options.Fps, _options.BitrateMbps, maxWidth, maxHeight, encoders[i]);
            var isLastOption = i == encoders.Length - 1;

            await using var source = FfmpegVideoSource.Start(_options.FfmpegPath, settings);
            Log.Info($"Encoder: {source.Name}, máx {maxWidth}x{maxHeight} @ {_options.Fps} fps, {_options.BitrateMbps} Mbps");

            var framesSent = await PumpAsync(source, channel, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var diagnostics = source.Diagnostics;
            if (framesSent > 0 || isLastOption)
            {
                throw new IOException($"El encoder {source.Name} se detuvo. {diagnostics}");
            }

            Log.Warn($"{source.Name} no produjo video, probando el siguiente encoder. {diagnostics}");
        }
    }

    /// <summary>Envía frames hasta que la fuente termina. Devuelve la cantidad enviada.</summary>
    private static async Task<long> PumpAsync(IVideoSource source, MessageChannel channel, CancellationToken ct)
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
                Log.Info($"Stream: {intervalFrames / seconds:F1} fps, {intervalBytes * 8 / seconds / 1_000_000:F1} Mbps");
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
