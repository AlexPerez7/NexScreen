using NexScreen.Core.Video;

namespace NexScreen.Core.Tests;

public class HostOptionsTests
{
    [Fact]
    public void UsesDefaults()
    {
        var options = HostOptions.Parse([]);

        Assert.Equal(27190, options.Port);
        Assert.Null(options.Fps);
        Assert.True(options.ScaleBitrate);
        Assert.False(options.AutoScale);
        Assert.Equal(EncoderKind.Auto, options.Encoder);
        Assert.Null(options.Position);
        Assert.True(options.AdaptiveBitrate);
    }

    [Fact]
    public void ParsesFixedBitrate()
    {
        Assert.False(HostOptions.Parse(["--fixed-bitrate"]).AdaptiveBitrate);
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
        Assert.False(options.ScaleBitrate);
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
    public void ParsesMediaFoundationEncoder()
    {
        Assert.Equal(EncoderKind.Mf, HostOptions.Parse(["--encoder", "mf"]).Encoder);
    }

    [Theory]
    [InlineData("--mode", "2")]
    [InlineData("--position", "middle")]
    [InlineData("--port", "0")]
    [InlineData("--fps", "abc")]
    [InlineData("--encoder", "vp9")]
    [InlineData("--scale", "50")]
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

    [Theory]
    [InlineData("nvenc", "Nvenc")]
    [InlineData("AMF", "Amf")]
    [InlineData("mfgpu", "MfGpu")]
    public void ParsesGpuEncoders(string value, string expected)
    {
        Assert.Equal(Enum.Parse<EncoderKind>(expected), HostOptions.Parse(["--encoder", value]).Encoder);
    }

    [Theory]
    [InlineData("auto", true, null)]
    [InlineData("175", false, 175)]
    public void ParsesScale(string value, bool auto, int? percent)
    {
        var options = HostOptions.Parse(["--scale", value]);

        Assert.Equal(auto, options.AutoScale);
        Assert.Equal(percent, options.ScalePercent);
    }
}
