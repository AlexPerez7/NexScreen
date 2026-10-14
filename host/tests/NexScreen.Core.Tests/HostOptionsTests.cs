using NexScreen.Core.Video;

namespace NexScreen.Core.Tests;

public class HostOptionsTests
{
    [Fact]
    public void UsesDefaults()
    {
        var options = HostOptions.Parse([]);

        Assert.Equal(27190, options.Port);
        Assert.Equal(60, options.Fps);
        Assert.Equal(EncoderKind.Auto, options.Encoder);
        Assert.Null(options.Position);
    }

    [Fact]
    public void ParsesAllOptions()
    {
        var options = HostOptions.Parse(
            ["--port", "3000", "--monitor", "1", "--fps", "30", "--bitrate", "20", "--encoder", "X264", "--verbose"]);

        Assert.Equal(3000, options.Port);
        Assert.Equal(1, options.Monitor);
        Assert.Equal(30, options.Fps);
        Assert.Equal(20, options.BitrateMbps);
        Assert.Equal(EncoderKind.X264, options.Encoder);
        Assert.True(options.Verbose);
    }

    [Fact]
    public void ParsesDisplayOptions()
    {
        var options = HostOptions.Parse(["--mode", "mirror", "--position", "LEFT", "--list-displays"]);

        Assert.Equal(ScreenMode.Mirror, options.Mode);
        Assert.Equal(NexScreen.Core.Display.DisplayPosition.Left, options.Position);
        Assert.True(options.ListDisplays);
    }

    [Theory]
    [InlineData(true, "Extend")]
    [InlineData(false, "Mirror")]
    public void AutoModeDependsOnVirtualDisplay(bool available, string expected)
    {
        var options = HostOptions.Parse([]);

        options.ResolveMode(available);

        Assert.Equal(Enum.Parse<ScreenMode>(expected), options.Mode);
    }

    [Fact]
    public void ExplicitModeIsNotOverridden()
    {
        var options = HostOptions.Parse(["--mode", "mirror"]);

        options.ResolveMode(virtualDisplayAvailable: true);

        Assert.Equal(ScreenMode.Mirror, options.Mode);
    }

    [Fact]
    public void BuildsMediaFoundationPipelineForDisplayRemoting()
    {
        var args = FfmpegVideoSource.BuildArguments(new VideoSourceSettings(0, 60, 12, 2304, 1440, EncoderKind.Mf));

        Assert.Contains("h264_mf", args);
        Assert.Equal("display_remoting", args[args.ToList().IndexOf("-scenario") + 1]);
        Assert.Contains(",hwdownload,format=bgra,", args[args.ToList().IndexOf("-i") + 1]);
    }

    [Fact]
    public void ParsesMediaFoundationEncoder()
    {
        Assert.Equal(EncoderKind.Mf, HostOptions.Parse(["--encoder", "mf"]).Encoder);
    }

    [Theory]
    [InlineData("--mode", "2")]
    [InlineData("--position", "middle")]
    [InlineData("--port", "0")]
    [InlineData("--fps", "abc")]
    [InlineData("--encoder", "nvenc")]
    [InlineData("--unknown", "1")]
    public void RejectsInvalidValues(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse([name, value]));
    }

    [Fact]
    public void RejectsMissingValue()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(["--port"]));
    }

    [Fact]
    public void BuildsQsvPipelineThatFitsClientWithoutUpscaling()
    {
        var args = FfmpegVideoSource.BuildArguments(new VideoSourceSettings(1, 60, 12, 2304, 1440, EncoderKind.Qsv));
        var graph = args[args.ToList().IndexOf("-i") + 1];

        Assert.StartsWith("ddagrab=output_idx=1:framerate=60", graph);
        Assert.Contains("scale_qsv=w='trunc(iw*min(1,min(2304/iw,1440/ih))/2)*2'", graph);
        Assert.Contains("h264_qsv", args);
        Assert.Equal("pipe:1", args[^1]);
    }
}
