using System.Diagnostics;
using NexScreen.Core.Display;
using NexScreen.Core.Protocol;
using NexScreen.Core.Video;

namespace NexScreen.Core.Net;

/// <summary>Lo que se transmite a este dispositivo: tamaño máximo, fps y bitrate máximo.</summary>
internal readonly record struct StreamFormat(int MaxWidth, int MaxHeight, int Fps, int MaxBitrateMbps);

/// <summary>
/// El video de una sesión: mantiene viva la captura, adapta el bitrate a la red y atiende los
/// pedidos de keyframe de la tablet.
///
/// ffmpeg no permite cambiar el bitrate ni forzar un keyframe en marcha, así que las dos cosas se
/// hacen con un reemplazo en caliente: se arranca un ffmpeg nuevo mientras el anterior sigue
/// transmitiendo y se pasa al nuevo cuando entrega su primer cuadro (SPS/PPS y luego un keyframe).
/// Desktop Duplication admite dos capturas de la misma pantalla a la vez.
/// </summary>
internal sealed class VideoStreamer
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BitrateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(30);

    /// <summary>Si la tablet insiste, no reiniciar la captura más seguido que esto.</summary>
    private static readonly TimeSpan MinKeyframeInterval = TimeSpan.FromSeconds(1);

    private const int MaxRestartsPerWindow = 10;
    private const int MaxStallRetries = 3;

    private readonly HostOptions _options;
    private readonly IStreamObserver _observer;
    private readonly MessageChannel _channel;
    private readonly Func<CaptureTarget> _resolveTarget;
    private readonly StreamFormat _format;
    private readonly BitrateController? _bitrate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _keyframeRequested;
    private TimeSpan? _lastSwitch;

    public VideoStreamer(
        HostOptions options, IStreamObserver observer, MessageChannel channel, Func<CaptureTarget> resolveTarget, StreamFormat format)
    {
        _options = options;
        _observer = observer;
        _channel = channel;
        _resolveTarget = resolveTarget;
        _format = format;
        _bitrate = options.AdaptiveBitrate ? new BitrateController(format.MaxBitrateMbps) : null;
    }

    private int CurrentBitrate => _bitrate?.CurrentMbps ?? _format.MaxBitrateMbps;

    /// <summary>La tablet no puede seguir decodificando: mandarle un keyframe. Se puede llamar desde cualquier hilo.</summary>
    public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeRequested, 1);

    /// <summary>
    /// Mantiene la captura viva. Si nunca produjo video prueba el siguiente encoder; si se cortó
    /// después de transmitir (Windows invalida Desktop Duplication al reordenar pantallas, cambiar
    /// la resolución, mostrar UAC o bloquear la sesión) la reinicia sin cortar la conexión.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        EncoderKind[] encoders = _options.Encoder == EncoderKind.Auto
            ? FfmpegVideoSource.AutoEncoders(_resolveTarget().Vendor)
            : [_options.Encoder];

        var encoderIndex = 0;
        var hasStreamed = false;
        var stallRetries = 0;
        var recentRestarts = new Queue<DateTime>();
        while (true)
        {
            // Una captura nueva empieza con un keyframe: los pedidos anteriores quedan atendidos.
            Interlocked.Exchange(ref _keyframeRequested, 0);
            var source = StartSource(Settings(encoders[encoderIndex], CurrentBitrate), ct);
            if (!hasStreamed && stallRetries == 0)
            {
                var mode = _bitrate is null ? "fijo" : "máximo, se adapta a la red";
                var fps = FfmpegVideoSource.EffectiveFps(source.Settings.Encoder, _format.Fps);
                Log.Info($"Encoder: {source.Source.Name}, máx {_format.MaxWidth}x{_format.MaxHeight} @ {fps} fps, " +
                         $"{CurrentBitrate} Mbps ({mode})");
            }

            var result = await PumpAsync(source, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Con la sesión bloqueada (o UAC en pantalla) Windows no deja capturar: esperar el
            // desbloqueo sin gastar reintentos ni culpar al encoder.
            if (result.Frames == 0 && SessionLock.IsLocked())
            {
                Log.Warn("La sesión de Windows está bloqueada; la transmisión sigue al desbloquear");
                await SessionLock.WaitUntilUnlockedAsync(ct).ConfigureAwait(false);
                Log.Info("Sesión desbloqueada, reanudando la captura");
                continue;
            }

            if (!hasStreamed && result.Frames == 0)
            {
                // ffmpeg vivo pero sin cuadros: no es una falla del encoder, reintentar con el mismo.
                if (result.Stalled && stallRetries < MaxStallRetries)
                {
                    stallRetries++;
                    Log.Warn($"La captura no arrancó, reintentando ({stallRetries}/{MaxStallRetries})");
                    await Task.Delay(RestartDelay, ct).ConfigureAwait(false);
                    continue;
                }

                if (encoderIndex == encoders.Length - 1)
                {
                    throw new IOException($"El encoder {result.SourceName} no produjo video. {result.Diagnostics}");
                }

                Log.Warn($"{result.SourceName} no produjo video, probando el siguiente encoder. {result.Diagnostics}");
                encoderIndex++;
                stallRetries = 0;
                continue;
            }

            hasStreamed = true;
            var now = DateTime.UtcNow;
            while (recentRestarts.Count > 0 && now - recentRestarts.Peek() > RestartWindow) recentRestarts.Dequeue();
            if (recentRestarts.Count >= MaxRestartsPerWindow)
            {
                throw new IOException($"La captura falló {MaxRestartsPerWindow} veces seguidas. {result.Diagnostics}");
            }

            recentRestarts.Enqueue(now);
            Log.Warn($"La captura se interrumpió ({FirstLine(result.Diagnostics)}); reiniciando");
            await Task.Delay(RestartDelay, ct).ConfigureAwait(false);
            // La causa más común es que el usuario movió el monitor: guardar dónde lo dejó.
            if (_options.Mode == ScreenMode.Extend) TryRememberPosition();
        }
    }

    private VideoSourceSettings Settings(EncoderKind encoder, int bitrateMbps)
    {
        var target = _resolveTarget();
        var bounds = DxgiOutputs.Bounds(target);
        return new(target, _format.Fps, bitrateMbps, _format.MaxWidth, _format.MaxHeight, encoder, bounds.Width, bounds.Height);
    }

    private RunningSource StartSource(VideoSourceSettings settings, CancellationToken ct) =>
        new(FfmpegVideoSource.Start(_options.FfmpegPath, settings), settings, ct);

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

    /// <summary>
    /// Cuadros enviados y si la fuente se colgó (seguía viva pero no entregó el primer cuadro),
    /// con el diagnóstico de la última fuente.
    /// </summary>
    private readonly record struct PumpResult(long Frames, bool Stalled, string SourceName, string Diagnostics);

    /// <summary>
    /// Envía cuadros hasta que la fuente termina, reemplazándola en caliente cuando hace falta otro
    /// bitrate o un keyframe. Siempre libera las fuentes que arrancó.
    /// </summary>
    private async Task<PumpResult> PumpAsync(RunningSource first, CancellationToken ct)
    {
        var current = first;
        RunningSource? pending = null;

        // Al bajar el bitrate por congestión, los cuadros de la captura vieja se descartan hasta que
        // entre la nueva: así la red se vacía en vez de seguir acumulando atraso.
        var dropping = false;
        var dropped = 0;
        var switchStarted = TimeSpan.Zero;
        int? requestedBitrate = null;

        long frames = 0, bytes = 0, statsFrames = 0, statsBytes = 0, windowBytes = 0;
        var windowBlocked = TimeSpan.Zero;
        var stats = Stopwatch.StartNew();
        var window = Stopwatch.StartNew();
        var stalled = false;
        try
        {
            while (true)
            {
                if (pending is null && current.Frames > 0 && TakeSwitch(ref requestedBitrate, current.Settings.BitrateMbps, out var bitrate, out var drop))
                {
                    pending = StartSource(Settings(current.Settings.Encoder, bitrate), ct);
                    dropping = drop;
                    dropped = 0;
                    switchStarted = _clock.Elapsed;
                    _lastSwitch = switchStarted;
                }

                if (pending is not null)
                {
                    await Task.WhenAny(current.NextAsync(), pending.NextAsync()).ConfigureAwait(false);
                    if (pending.NextAsync().IsCompleted)
                    {
                        if (!await HasFrameAsync(pending, ct, tolerateErrors: true).ConfigureAwait(false))
                        {
                            // Volver a la captura vieja no sirve si ya se descartaron cuadros (la tablet
                            // necesita un keyframe), y su bitrate ya no es el esperado: reiniciar.
                            Log.Warn($"La captura de reemplazo no arrancó: {FirstLine(pending.Source.Diagnostics)}");
                            break;
                        }

                        await current.DisposeAsync().ConfigureAwait(false);
                        current = pending;
                        pending = null;
                        Interlocked.Exchange(ref _keyframeRequested, 0);
                        var ms = (_clock.Elapsed - switchStarted).TotalMilliseconds;
                        Log.Debug($"Captura reemplazada en {ms:F0} ms ({dropped} cuadros descartados)");
                        dropping = false;
                        continue;
                    }
                }

                if (!await HasFrameAsync(current, ct, tolerateErrors: false).ConfigureAwait(false))
                {
                    stalled = current.Frames == 0 && current.TimedOut && !ct.IsCancellationRequested;
                    break;
                }

                var frame = current.Take();
                if (dropping)
                {
                    dropped++;
                    continue;
                }

                var writeStart = _clock.Elapsed;
                await _channel.WriteVideoAsync(frame, ct).ConfigureAwait(false);
                windowBlocked += _clock.Elapsed - writeStart;

                frames++;
                bytes += frame.Data.Length;
                windowBytes += frame.Data.Length;
                if ((frame.Flags & FrameFlags.Config) == 0) statsFrames++;
                statsBytes += frame.Data.Length;

                if (window.Elapsed >= BitrateWindow)
                {
                    var seconds = window.Elapsed.TotalSeconds;
                    var ratio = windowBlocked.TotalSeconds / seconds;
                    var sentMbps = windowBytes * 8 / seconds / 1_000_000;
                    var previous = CurrentBitrate;
                    if (_bitrate?.OnWindow(ratio, sentMbps, _clock.Elapsed) is { } next)
                    {
                        Log.Info(next < previous
                            ? $"La red no da abasto ({ratio:P0} del tiempo esperando, {sentMbps:F1} Mbps enviados): bitrate {previous} → {next} Mbps"
                            : $"La red aguanta: bitrate {previous} → {next} Mbps");
                        requestedBitrate = next;
                    }

                    windowBlocked = TimeSpan.Zero;
                    windowBytes = 0;
                    window.Restart();
                }

                if (stats.Elapsed >= StatsInterval)
                {
                    var seconds = stats.Elapsed.TotalSeconds;
                    var fps = statsFrames / seconds;
                    var mbps = statsBytes * 8 / seconds / 1_000_000;
                    Log.Info($"Stream: {fps:F1} fps, {mbps:F1} Mbps (bitrate {CurrentBitrate} Mbps)");
                    _observer.OnStats(new StreamStats(fps, mbps, CurrentBitrate, _format.MaxBitrateMbps));
                    statsFrames = statsBytes = 0;
                    stats.Restart();
                }
            }
        }
        finally
        {
            if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false);
        }

        var name = current.Source.Name;
        var diagnostics = current.Source.Diagnostics;
        await current.DisposeAsync().ConfigureAwait(false);

        if (stalled) Log.Warn($"{name} no entregó el primer frame en {FirstFrameTimeout.TotalSeconds:F0} s");
        Log.Debug($"Fuente terminada: {frames} frames, {bytes / 1024} KiB");
        return new PumpResult(frames, stalled, name, diagnostics);
    }

    /// <summary>
    /// Decide si hay que reemplazar la captura: por un bitrate nuevo (al bajarlo se descartan los
    /// cuadros viejos) o porque la tablet pidió un keyframe.
    /// </summary>
    private bool TakeSwitch(ref int? requestedBitrate, int runningBitrate, out int bitrate, out bool dropOld)
    {
        if (requestedBitrate is { } requested)
        {
            requestedBitrate = null;
            bitrate = requested;
            dropOld = requested < runningBitrate;
            return true;
        }

        bitrate = CurrentBitrate;
        dropOld = false;
        if (Volatile.Read(ref _keyframeRequested) == 0) return false;
        if (_lastSwitch is { } last && _clock.Elapsed - last < MinKeyframeInterval) return false;

        Interlocked.Exchange(ref _keyframeRequested, 0);
        Log.Info("El dispositivo pidió un keyframe");
        return true;
    }

    /// <summary>
    /// Espera el próximo cuadro. False si la fuente terminó (o se canceló por no arrancar a tiempo);
    /// con <paramref name="tolerateErrors"/> también si falló.
    /// </summary>
    private static async Task<bool> HasFrameAsync(RunningSource source, CancellationToken ct, bool tolerateErrors)
    {
        try
        {
            return await source.NextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (tolerateErrors && ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Una captura en marcha, leída de a un cuadro para poder esperar dos a la vez. Si no entrega el
    /// primer cuadro a tiempo se cancela, lo que mata a ffmpeg y termina la lectura.
    /// </summary>
    private sealed class RunningSource : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly IAsyncEnumerator<EncodedFrame> _frames;
        private Task<bool>? _next;

        public RunningSource(IVideoSource source, VideoSourceSettings settings, CancellationToken ct)
        {
            Source = source;
            Settings = settings;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cts.CancelAfter(FirstFrameTimeout);
            _frames = source.ReadFramesAsync(_cts.Token).GetAsyncEnumerator(_cts.Token);
        }

        public IVideoSource Source { get; }

        public VideoSourceSettings Settings { get; }

        public long Frames { get; private set; }

        /// <summary>Se canceló sin haber entregado ningún cuadro.</summary>
        public bool TimedOut => Frames == 0 && _cts.IsCancellationRequested;

        /// <summary>La lectura del próximo cuadro; se reusa hasta que se llama a <see cref="Take"/>.</summary>
        public Task<bool> NextAsync() => _next ??= _frames.MoveNextAsync().AsTask();

        public EncodedFrame Take()
        {
            _next = null;
            if (Frames++ == 0) _cts.CancelAfter(Timeout.InfiniteTimeSpan);
            return _frames.Current;
        }

        public async ValueTask DisposeAsync()
        {
            // Cancelar mata a ffmpeg: la lectura en curso termina y recién ahí se puede liberar el enumerador.
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_next is not null)
            {
                try
                {
                    await _next.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
                {
                    // Esperado al matar el proceso.
                }
            }

            await _frames.DisposeAsync().ConfigureAwait(false);
            await Source.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
