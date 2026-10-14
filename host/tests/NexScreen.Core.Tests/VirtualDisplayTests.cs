using NexScreen.Core.Display;

namespace NexScreen.Core.Tests;

public class VirtualDisplayTests
{
    private static readonly DisplayInfo Primary =
        new(@"\\.\DISPLAY1", "Intel(R) UHD Graphics", "PCI\\VEN_8086", true, true, 0, 0, 1920, 1200, 60);

    private static readonly DisplayInfo Secondary =
        new(@"\\.\DISPLAY2", "Intel(R) UHD Graphics", "PCI\\VEN_8086", true, false, 1920, -200, 2560, 1440, 60);

    [Theory]
    [InlineData("Right", 1920, 0)]
    [InlineData("Left", -2304, 0)]
    [InlineData("Above", 0, -1440)]
    [InlineData("Below", 0, 1200)]
    public void PlacesNextToPrimary(string position, int x, int y)
    {
        Assert.Equal((x, y), VirtualDisplay.ComputePosition([Primary], 2304, 1440, Enum.Parse<DisplayPosition>(position)));
    }

    [Fact]
    public void PlacesRightOfTheRightmostMonitor()
    {
        Assert.Equal((4480, 0), VirtualDisplay.ComputePosition([Primary, Secondary], 2304, 1440, DisplayPosition.Right));
    }

    [Fact]
    public void PlacesAboveTheTopmostMonitor()
    {
        Assert.Equal((0, -1640), VirtualDisplay.ComputePosition([Primary, Secondary], 2304, 1440, DisplayPosition.Above));
    }

    [Theory]
    [InlineData("MttVDD", "ROOT\\MttVDD\\0000", true)]
    [InlineData("Virtual Display Driver", "ROOT\\DISPLAY\\0000", true)]
    [InlineData("Intel(R) UHD Graphics", "PCI\\VEN_8086", false)]
    public void DetectsVirtualDisplay(string adapter, string deviceId, bool expected)
    {
        var display = new DisplayInfo(@"\\.\DISPLAY9", adapter, deviceId, false, false, 0, 0, 0, 0, 0);

        Assert.Equal(expected, VirtualDisplay.IsVirtual(display));
    }
}

public class VddSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vdd_settings_{Guid.NewGuid():N}.xml");

    public VddSettingsTests()
    {
        File.WriteAllText(_path, """
            <?xml version='1.0' encoding='utf-8'?>
            <vdd_settings>
                <monitors><count>1</count></monitors>
                <resolutions>
                    <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
                </resolutions>
            </vdd_settings>
            """);
    }

    [Fact]
    public void AddsMissingResolutionAsPreferred()
    {
        Assert.True(VddSettings.EnsureResolution(2304, 1440, 60, _path));

        var widths = System.Xml.Linq.XDocument.Load(_path).Descendants("width").Select(w => (int)w).ToList();
        Assert.Equal([2304, 1920], widths);
    }

    [Fact]
    public void LeavesExistingResolutionUntouched()
    {
        var before = File.ReadAllText(_path);

        Assert.False(VddSettings.EnsureResolution(1920, 1080, 60, _path));
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void TreatsDifferentRefreshRateAsNewMode()
    {
        Assert.True(VddSettings.EnsureResolution(1920, 1080, 90, _path));
    }

    public void Dispose() => File.Delete(_path);
}
