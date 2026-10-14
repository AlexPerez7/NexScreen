using NexScreen.Core.Display;
using NexScreen.Core.Video;

namespace NexScreen.Core.Tests;

public class StreamTuningTests
{
    [Theory]
    [InlineData(null, 0, 60)] // App vieja: no informa la frecuencia.
    [InlineData(null, 90, 90)] // Galaxy Tab S9 FE.
    [InlineData(null, 120, 120)] // Galaxy S25 Ultra.
    [InlineData(null, 144, 120)]
    [InlineData(null, 24, 30)]
    [InlineData(30, 120, 30)] // --fps gana.
    public void ChoosesClientRefreshRate(int? requested, int refreshRate, int expected)
    {
        Assert.Equal(expected, StreamTuning.ChooseFps(requested, refreshRate));
    }

    [Theory]
    [InlineData(2304, 1440, 60, 12)] // Referencia: no cambia.
    [InlineData(2304, 1440, 90, 15)]
    [InlineData(3120, 1440, 120, 23)] // Celular grande a 120 Hz.
    [InlineData(1280, 800, 60, 4)]
    [InlineData(640, 360, 60, 2)] // Piso.
    [InlineData(7680, 4320, 120, 80)] // Techo.
    public void ScalesBitrateWithPixelsAndFps(int width, int height, int fps, int expected)
    {
        Assert.Equal(expected, StreamTuning.ScaleBitrate(12, width, height, fps));
    }

    [Theory]
    [InlineData(280, 150)] // Tablet de referencia.
    [InlineData(160, 100)] // mdpi: nunca por debajo de 100 %.
    [InlineData(320, 175)]
    [InlineData(450, 250)] // Celular FHD+.
    [InlineData(600, 300)] // Celular QHD+.
    [InlineData(0, 150)] // Sin dato.
    public void ScalesWindowsDpiWithAndroidDensity(int dpi, int expected)
    {
        Assert.Equal(expected, StreamTuning.AutoScalePercent(dpi));
    }
}

public class FfmpegArgumentsTests
{
    private static readonly CaptureTarget IntelOutput1 = new(Adapter: 0, Output: 1, GpuVendor.Intel);
    private static readonly CaptureTarget NvidiaOutput0 = new(Adapter: 1, Output: 0, GpuVendor.Nvidia);

    private static VideoSourceSettings Settings(
        EncoderKind encoder, CaptureTarget target, int fps = 60, int captureWidth = 2304, int captureHeight = 1440) =>
        new(target, fps, 12, 2304, 1440, encoder, captureWidth, captureHeight);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    private static string ValueOf(IReadOnlyList<string> args, string name) => args[args.ToList().IndexOf(name) + 1];

    [Fact]
    public void CapturesWithTheDisplaysAdapter()
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(EncoderKind.Nvenc, NvidiaOutput0));

        Assert.Equal("d3d11va=nexscreen:1", ValueOf(args, "-init_hw_device"));
        Assert.Equal("nexscreen", ValueOf(args, "-filter_hw_device"));
        Assert.StartsWith("ddagrab=output_idx=0:framerate=60", Graph(args));
        Assert.EndsWith("[video]", Graph(args));
        Assert.Equal("[video]", ValueOf(args, "-map"));
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void BuildsQsvPipelineThatFitsClientWithoutUpscaling()
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(EncoderKind.Qsv, IntelOutput1));

        Assert.StartsWith("ddagrab=output_idx=1:framerate=60", Graph(args));
        Assert.Contains("scale_qsv=w='trunc(iw*min(1,min(2304/iw,1440/ih))/2)*2'", Graph(args));
        Assert.Contains("h264_qsv", args);
    }

    [Theory]
    [InlineData("Nvenc", "h264_nvenc")]
    [InlineData("Amf", "h264_amf")]
    [InlineData("MfGpu", "h264_mf")]
    public void GpuEncodersTakeTheCapturedTextureWhenItFits(string encoder, string codec)
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(Enum.Parse<EncoderKind>(encoder), NvidiaOutput0));

        Assert.Equal("ddagrab=output_idx=0:framerate=60:draw_mouse=1[video]", Graph(args));
        Assert.Equal(codec, ValueOf(args, "-c:v"));
    }

    [Fact]
    public void GpuEncodersScaleOnCpuWhenTheDisplayIsLarger()
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(EncoderKind.Nvenc, NvidiaOutput0, captureWidth: 3840, captureHeight: 2160));

        Assert.Contains(",hwdownload,format=bgra,scale=", Graph(args));
        Assert.EndsWith(",format=nv12[video]", Graph(args));
    }

    [Fact]
    public void BuildsMediaFoundationPipelineForDisplayRemoting()
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(EncoderKind.Mf, IntelOutput1));

        Assert.Equal("h264_mf", ValueOf(args, "-c:v"));
        Assert.Equal("0", ValueOf(args, "-hw_encoding"));
        Assert.Equal("display_remoting", ValueOf(args, "-scenario"));
        Assert.Contains(",hwdownload,format=bgra,", Graph(args));
    }

    [Theory]
    [InlineData("Mf", 60)]
    [InlineData("X264", 60)]
    [InlineData("Qsv", 120)]
    [InlineData("Nvenc", 120)]
    public void CpuEncodersAreCappedAt60Fps(string encoder, int expected)
    {
        var args = FfmpegVideoSource.BuildArguments(Settings(Enum.Parse<EncoderKind>(encoder), IntelOutput1, fps: 120));

        Assert.StartsWith($"ddagrab=output_idx=1:framerate={expected}:", Graph(args));
        Assert.Equal((expected * 2).ToString(), ValueOf(args, "-g"));
    }

    [Theory]
    [InlineData("Intel", "Qsv")]
    [InlineData("Nvidia", "Nvenc")]
    [InlineData("Amd", "Amf")]
    [InlineData("Unknown", "MfGpu")]
    public void PrefersTheEncoderOfTheDisplaysGpu(string vendor, string first)
    {
        var encoders = FfmpegVideoSource.AutoEncoders(Enum.Parse<GpuVendor>(vendor));

        Assert.Equal(Enum.Parse<EncoderKind>(first), encoders[0]);
        Assert.Equal([EncoderKind.MfGpu, EncoderKind.Mf, EncoderKind.X264], encoders[^3..]);
    }

    [Theory]
    [InlineData(0x8086u, "Intel")]
    [InlineData(0x10DEu, "Nvidia")]
    [InlineData(0x1002u, "Amd")]
    [InlineData(0x1414u, "Unknown")] // Microsoft (Basic Render Driver).
    public void RecognizesGpuVendors(uint vendorId, string expected)
    {
        Assert.Equal(Enum.Parse<GpuVendor>(expected), DxgiOutputs.VendorOf(vendorId));
    }
}
