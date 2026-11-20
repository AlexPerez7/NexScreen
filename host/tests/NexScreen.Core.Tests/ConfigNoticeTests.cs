using System.Text.Json;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Net;
using NexScreen.Core.Protocol;

namespace NexScreen.Core.Tests;

public class ConfigNoticeTests
{
    [Theory]
    [InlineData("Working", "Auto", false)]
    [InlineData("Working", "Extend", false)]
    [InlineData("Broken", "Mirror", false)] // pidió espejo: no hay nada que avisar
    [InlineData("NotInstalled", "Mirror", false)]
    [InlineData("Broken", "Auto", true)]
    [InlineData("Broken", "Extend", true)]
    [InlineData("NotInstalled", "Auto", true)]
    public void NoticeOnlyWhenMirrorIsNotWhatTheUserAskedFor(string driver, string requested, bool expectNotice)
    {
        var notice = ClientSession.MirrorNotice(Enum.Parse<DriverState>(driver), Enum.Parse<ScreenMode>(requested));

        Assert.Equal(expectNotice, notice is not null);
    }

    [Fact]
    public void BrokenAndNotInstalledHaveDifferentNotices()
    {
        Assert.NotEqual(
            ClientSession.MirrorNotice(DriverState.Broken, ScreenMode.Auto),
            ClientSession.MirrorNotice(DriverState.NotInstalled, ScreenMode.Auto));
    }

    [Fact]
    public void ConfigOmitsTheNoticeWhenThereIsNone()
    {
        var json = JsonSerializer.Serialize(new ConfigMessage(2, "h264", 2304, 1440, 60), Wire.Json);

        Assert.DoesNotContain("notice", json);
    }

    [Fact]
    public void ConfigCarriesTheNotice()
    {
        var json = JsonSerializer.Serialize(new ConfigMessage(2, "h264", 2304, 1440, 60, "hola"), Wire.Json);

        Assert.Contains("\"notice\":\"hola\"", json);
    }
}
