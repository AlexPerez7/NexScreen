using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using NexScreen.Core.Display;

namespace NexScreen.Core.Video;

/// <param name="CaptureWidth">Tamaño de la pantalla capturada: si entra en el máximo no hace falta escalar.</param>
internal sealed record VideoSourceSettings(
    CaptureTarget Target,
    int Fps,
    int BitrateMbps,
    int MaxWidth,
    int MaxHeight,
    EncoderKind Encoder,
    int CaptureWidth,
    int CaptureHeight);

/// <summary>
/// Captura con ddagrab (Desktop Duplication) y codifica H.264 con un proceso ffmpeg.
/// Implementación de la fase 1; a futuro se puede reemplazar por un pipeline nativo
/// Media Foundation detrás de <see cref="IVideoSource"/>.
/// </summary>
internal sealed class FfmpegVideoSource : IVideoSource
{
    private const int DiagnosticLines = 20;

    private readonly Process _process;
    private readonly Queue<string> _stderrTail = new();

    private FfmpegVideoSource(Process process, EncoderKind encoder)
    {
        _process = process;
        Name = DisplayName(encoder);
    }

    public static string DisplayName(EncoderKind encoder) => encoder switch
    {
        EncoderKind.Qsv => "Intel Quick Sync (h264_qsv)",
        EncoderKind.Nvenc => "NVIDIA NVENC (h264_nvenc)",
        EncoderKind.Amf => "AMD AMF (h264_amf)",
        EncoderKind.MfGpu => "Media Foundation por GPU (h264_mf)",
        EncoderKind.Mf => "Media Foundation por CPU (h264_mf)",
        _ => "x264 (CPU)",
    };

    /// <summary>
    /// Encoders a probar en orden con <see cref="EncoderKind.Auto"/>: primero el del fabricante de la
    /// GPU que tiene la pantalla (los cuadros capturados ya están en esa GPU), después Media
    /// Foundation por GPU (cualquier fabricante) y al final los de CPU.
    /// </summary>
    public static EncoderKind[] AutoEncoders(GpuVendor vendor)
    {
        EncoderKind[] fallback = [EncoderKind.MfGpu, EncoderKind.Mf, EncoderKind.X264];
        return vendor switch
        {
            GpuVendor.Intel => [EncoderKind.Qsv, .. fallback],
            GpuVendor.Nvidia => [EncoderKind.Nvenc, .. fallback],
            GpuVendor.Amd => [EncoderKind.Amf, .. fallback],
            _ => fallback,
        };
    }

    /// <summary>Los encoders por CPU no sostienen más de 60 fps a la resolución de una tablet.</summary>
    public static int EffectiveFps(EncoderKind encoder, int fps) =>
        encoder is EncoderKind.Mf or EncoderKind.X264 ? Math.Min(fps, SoftwareMaxFps) : fps;

    private const int SoftwareMaxFps = 60;

    public string Name { get; }

    public string Diagnostics
    {
        get
        {
            lock (_stderrTail) return string.Join(Environment.NewLine, _stderrTail);
        }
    }

    public static FfmpegVideoSource Start(string ffmpegPath, VideoSourceSettings settings)
    {
        if (settings.Encoder == EncoderKind.Auto) throw new ArgumentException("El encoder debe ser concreto", nameof(settings));

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(settings)) startInfo.ArgumentList.Add(argument);

        Log.Debug($"ffmpeg {string.Join(' ', startInfo.ArgumentList)}");
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg");
        var source = new FfmpegVideoSource(process, settings.Encoder);
        process.ErrorDataReceived += (_, e) => source.OnStderr(e.Data);
        process.BeginErrorReadLine();
        return source;
    }

    public async IAsyncEnumerable<EncodedFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Las lecturas del pipe de stdout no respetan la cancelación: matar el proceso
        // cierra el pipe y la lectura termina.
        await using var registration = ct.Register(Kill);
        await foreach (var frame in new FlvReader(_process.StandardOutput.BaseStream).ReadFramesAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Ya terminó.
        }
    }

    internal static IReadOnlyList<string> BuildArguments(VideoSourceSettings s)
    {
        var fpsValue = EffectiveFps(s.Encoder, s.Fps);
        var fps = fpsValue.ToString(CultureInfo.InvariantCulture);
        var gop = (fpsValue * 2).ToString(CultureInfo.InvariantCulture);
        var bitrate = $"{s.BitrateMbps}M";
        var bufsize = $"{Math.Max(1, s.BitrateMbps / 2)}M";

        // Escala para que entre en la pantalla del cliente manteniendo el aspecto, sin agrandar.
        var factor = $"min(1,min({s.MaxWidth}/iw,{s.MaxHeight}/ih))";
        var width = $"'trunc(iw*{factor}/2)*2'";
        var height = $"'trunc(ih*{factor}/2)*2'";
        var cpuScale = $"hwdownload,format=bgra,scale=w={width}:h={height}:flags=bilinear";
        var fits = s.CaptureWidth <= s.MaxWidth && s.CaptureHeight <= s.MaxHeight;
        var capture = $"ddagrab=output_idx={s.Target.Output}:framerate={fps}:draw_mouse=1";

        string graph;
        var encoder = new List<string>();
        switch (s.Encoder)
        {
            case EncoderKind.Qsv:
                graph = $"{capture},hwmap=derive_device=qsv,format=qsv,scale_qsv=w={width}:h={height}:format=nv12";
                encoder.AddRange([
                    "-c:v", "h264_qsv",
                    "-preset", "veryfast",
                    "-profile:v", "main",
                    "-bf", "0",
                    "-async_depth", "1",
                    "-look_ahead", "0",
                    "-forced_idr", "1",
                ]);
                break;
            case EncoderKind.Nvenc:
                // NVENC y AMF toman la textura de ddagrab sin copiarla. Si hay que achicar (modo espejo
                // de una pantalla más grande que el dispositivo) se escala por CPU: scale_d3d11 no es
                // confiable en todos los drivers.
                graph = fits ? capture : $"{capture},{cpuScale},format=nv12";
                encoder.AddRange([
                    "-c:v", "h264_nvenc",
                    "-preset", "p1",
                    "-tune", "ull",
                    "-rc", "cbr",
                    "-zerolatency", "1",
                    "-delay", "0",
                    "-profile:v", "main",
                    "-bf", "0",
                    "-forced-idr", "1",
                ]);
                break;
            case EncoderKind.Amf:
                graph = fits ? capture : $"{capture},{cpuScale},format=nv12";
                encoder.AddRange([
                    "-c:v", "h264_amf",
                    "-usage", "ultralowlatency",
                    "-quality", "speed",
                    "-rc", "cbr",
                    "-profile:v", "main",
                    "-bf", "0",
                ]);
                break;
            case EncoderKind.MfGpu or EncoderKind.Mf:
                var gpu = s.Encoder == EncoderKind.MfGpu;
                graph = gpu && fits ? capture : $"{capture},{cpuScale},format=nv12";
                encoder.AddRange([
                    "-c:v", "h264_mf",
                    // Por GPU usa el MFT del fabricante; por CPU, el de Windows, que está en todos lados.
                    "-hw_encoding", gpu ? "1" : "0",
                    // Modo de Windows pensado para escritorio remoto: baja latencia.
                    "-scenario", "display_remoting",
                    "-rate_control", "cbr",
                ]);
                break;
            default:
                graph = $"{capture},{cpuScale},format=yuv420p";
                encoder.AddRange([
                    "-c:v", "libx264",
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-profile:v", "main",
                ]);
                break;
        }

        // ddagrab captura con el dispositivo D3D11 que recibe: tiene que ser la GPU de la pantalla.
        var adapter = s.Target.Adapter.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-init_hw_device", $"d3d11va=nexscreen:{adapter}", "-filter_hw_device", "nexscreen",
            "-filter_complex", $"{graph}[video]", "-map", "[video]",
        };
        args.AddRange(encoder);

        args.AddRange([
            "-g", gop,
            "-b:v", bitrate,
            "-maxrate", bitrate,
            "-bufsize", bufsize,
            "-an",
            "-f", "flv",
            "-flvflags", "no_duration_filesize",
            "-flush_packets", "1",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "pipe:1",
        ]);
        return args;
    }

    private void OnStderr(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Log.Debug($"[ffmpeg] {line}");
        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > DiagnosticLines) _stderrTail.Dequeue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Kill();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Log.Warn($"ffmpeg no terminó limpiamente: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
        }
    }
}
