using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NexScreen.Core.Display;

namespace NexScreen.Core.Video.Native;

/// <summary>
/// Captura y codificación sin ffmpeg: Desktop Duplication → VideoProcessor (NV12 en la GPU) → NVENC o
/// el encoder de hardware de Media Foundation. Un hilo propio hace todo el trabajo y deja los cuadros
/// en una cola; el keyframe a pedido y el cambio de bitrate se atienden en marcha, sin reemplazar la
/// captura. Si no puede arrancar (otra GPU, driver sin encoder) la fuente termina vacía con el motivo
/// en <see cref="Diagnostics"/>, y el streamer prueba la siguiente opción.
/// </summary>
internal sealed class NativeVideoSource : IVideoSource
{
    /// <summary>Cuadros que pueden esperar en la cola: más que eso significa que el destino no da abasto.</summary>
    private const int MaxQueued = 3;

    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(250);

    private readonly VideoSourceSettings _settings;
    private readonly Channel<EncodedFrame> _frames = Channel.CreateBounded<EncodedFrame>(
        new BoundedChannelOptions(MaxQueued * 4) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _diagnosticsLock = new();
    private string _diagnostics = "";
    private DesktopCapture? _capture;
    private IGpuEncoder? _encoder;
    private Thread? _thread;
    private int _keyFrameRequested;
    private int _bitrateRequested;

    private NativeVideoSource(VideoSourceSettings settings)
    {
        _settings = settings;
        Name = "Captura nativa";
    }

    public string Name { get; private set; }

    public string Diagnostics
    {
        get
        {
            lock (_diagnosticsLock) return _diagnostics;
        }
    }

    /// <summary>Esta fuente atiende el keyframe y el bitrate sin reiniciarse.</summary>
    public bool SupportsLiveControl => true;

    public void RequestKeyFrame() => Interlocked.Exchange(ref _keyFrameRequested, 1);

    public void SetBitrate(int mbps) => Interlocked.Exchange(ref _bitrateRequested, mbps);

    /// <summary>
    /// Prepara la captura y el encoder en el hilo que llama (para saber enseguida cuál se usa) y arranca
    /// el hilo de trabajo. Nunca lanza: si algo falla, la fuente queda terminada con el diagnóstico.
    /// </summary>
    public static NativeVideoSource Start(VideoSourceSettings settings)
    {
        var source = new NativeVideoSource(settings);
        try
        {
            source.Initialize();
            source._thread = new Thread(source.Run) { IsBackground = true, Name = "nexscreen-capture" };
            source._thread.Start();
        }
        catch (Exception ex)
        {
            source.SetDiagnostics($"{ex.GetType().Name}: {ex.Message}");
            source.Cleanup();
            source._frames.Writer.TryComplete();
        }

        return source;
    }

    private void Initialize()
    {
        _capture = DesktopCapture.Create(_settings.Target, _settings.MaxWidth, _settings.MaxHeight);
        _encoder = CreateEncoder(_capture);
        Name = $"{_encoder.Name} (nativo)";
        Log.Debug($"Captura nativa {_capture.CaptureWidth}x{_capture.CaptureHeight} → {_capture.OutWidth}x{_capture.OutHeight}, {_encoder.Name}");
    }

    private IGpuEncoder CreateEncoder(DesktopCapture capture)
    {
        var fps = FfmpegVideoSource.EffectiveFps(_settings.Encoder, _settings.Fps);
        switch (_settings.Target.Vendor)
        {
            case GpuVendor.Nvidia:
                var api = NvEncApi.TryLoad(out var error) ?? throw new NotSupportedException(error);
                return NvencEncoder.Create(api, capture.Device, capture.OutWidth, capture.OutHeight, fps, _settings.BitrateMbps);
            case GpuVendor.Intel:
                return MfH264Encoder.Create(capture.Device, "Intel", capture.OutWidth, capture.OutHeight, fps, _settings.BitrateMbps);
            case GpuVendor.Amd:
                return MfH264Encoder.Create(capture.Device, "AMD", capture.OutWidth, capture.OutHeight, fps, _settings.BitrateMbps);
            default:
                throw new NotSupportedException("Fabricante de GPU sin encoder nativo");
        }
    }

    public async IAsyncEnumerable<EncodedFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var registration = ct.Register(() => _stop.Cancel());
        while (await _frames.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_frames.Reader.TryRead(out var frame)) yield return frame;
        }
    }

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            using var timer = TimerResolution.Request();
            Capture(token);
        }
        catch (CaptureLostException ex)
        {
            SetDiagnostics(ex.Message);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            SetDiagnostics($"{ex.GetType().Name}: {ex.Message}");
            Log.Warn($"La captura nativa se detuvo: {ex}");
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    private void Capture(CancellationToken token)
    {
        var capture = _capture!;
        var encoder = _encoder!;
        var fps = FfmpegVideoSource.EffectiveFps(_settings.Encoder, _settings.Fps);
        var pacer = new FramePacer(fps, Heartbeat);
        var clock = Stopwatch.StartNew();
        var output = new List<EncodedFrame>(2);
        var configSent = false;
        var hasFrame = false;
        var changed = false;
        var first = true;

        while (!token.IsCancellationRequested)
        {
            var bitrate = Interlocked.Exchange(ref _bitrateRequested, 0);
            if (bitrate > 0) encoder.SetBitrate(bitrate);

            var keyRequested = Volatile.Read(ref _keyFrameRequested) == 1;
            var decision = pacer.Next(clock.Elapsed, hasFrame, changed, keyRequested, _frames.Reader.Count >= MaxQueued);
            if (decision.Send)
            {
                var force = first || Interlocked.Exchange(ref _keyFrameRequested, 0) == 1;
                first = false;
                output.Clear();
                encoder.Encode(capture.Convert(), clock.Elapsed.Ticks / 10, force, output);
                pacer.Sent(clock.Elapsed);
                changed = false;

                foreach (var frame in output)
                {
                    // El cuadro de configuración (SPS/PPS) va antes del primer keyframe.
                    if (!configSent && encoder.ParameterSets is { } parameters)
                    {
                        _frames.Writer.TryWrite(new EncodedFrame(parameters, frame.PtsUs, FrameFlags.Config));
                        configSent = true;
                    }

                    if (configSent) _frames.Writer.TryWrite(frame);
                }

                continue;
            }

            if (capture.Acquire(decision.WaitMs) == AcquireStatus.Updated)
            {
                hasFrame = true;
                changed = true;
            }
        }
    }

    private void SetDiagnostics(string text)
    {
        lock (_diagnosticsLock) _diagnostics = text;
    }

    private void Cleanup()
    {
        _encoder?.Dispose();
        _capture?.Dispose();
        _encoder = null;
        _capture = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_thread is { } thread) await Task.Run(() => thread.Join(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        Cleanup();
        _stop.Dispose();
    }
}
