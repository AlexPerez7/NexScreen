using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NexScreen.Core.Video;

internal sealed record VideoSourceSettings(
    int Monitor,
    int Fps,
    int BitrateMbps,
    int MaxWidth,
    int MaxHeight,
    EncoderKind Encoder);

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
        Name = encoder switch
        {
            EncoderKind.Qsv => "Quick Sync (h264_qsv)",
            EncoderKind.Mf => "Media Foundation (h264_mf)",
            _ => "x264 (CPU)",
        };
    }

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
        var fps = s.Fps.ToString(CultureInfo.InvariantCulture);
        var gop = (s.Fps * 2).ToString(CultureInfo.InvariantCulture);
        var bitrate = $"{s.BitrateMbps}M";
        var bufsize = $"{Math.Max(1, s.BitrateMbps / 2)}M";

        // Escala para que entre en la pantalla del cliente manteniendo el aspecto, sin agrandar.
        var factor = $"min(1,min({s.MaxWidth}/iw,{s.MaxHeight}/ih))";
        var width = $"'trunc(iw*{factor}/2)*2'";
        var height = $"'trunc(ih*{factor}/2)*2'";
        var capture = $"ddagrab=output_idx={s.Monitor}:framerate={fps}:draw_mouse=1";

        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "lavfi", "-i" };

        if (s.Encoder == EncoderKind.Qsv)
        {
            args.Add($"{capture},hwmap=derive_device=qsv,format=qsv,scale_qsv=w={width}:h={height}:format=nv12");
            args.AddRange([
                "-c:v", "h264_qsv",
                "-preset", "veryfast",
                "-profile:v", "main",
                "-bf", "0",
                "-async_depth", "1",
                "-look_ahead", "0",
                "-forced_idr", "1",
            ]);
        }
        else if (s.Encoder == EncoderKind.Mf)
        {
            args.Add($"{capture},hwdownload,format=bgra,scale=w={width}:h={height}:flags=bilinear,format=nv12");
            args.AddRange([
                "-c:v", "h264_mf",
                // Por software: el MFT de hardware puede no existir; este está en todo Windows.
                "-hw_encoding", "0",
                // Modo de Windows pensado para escritorio remoto: baja latencia.
                "-scenario", "display_remoting",
                "-rate_control", "cbr",
            ]);
        }
        else
        {
            args.Add($"{capture},hwdownload,format=bgra,scale=w={width}:h={height}:flags=bilinear,format=yuv420p");
            args.AddRange([
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-tune", "zerolatency",
                "-profile:v", "main",
            ]);
        }

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
