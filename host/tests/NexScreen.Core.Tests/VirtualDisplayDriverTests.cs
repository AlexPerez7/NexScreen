using NexScreen.Core.Display;

namespace NexScreen.Core.Tests;

public class VirtualDisplayDriverTests
{
    [Theory]
    [InlineData(true, true, "Working")]
    [InlineData(false, true, "Working")]
    [InlineData(true, false, "Broken")]
    [InlineData(false, false, "NotInstalled")]
    public void EvaluatesDriverState(bool deviceInstalled, bool adapterPresent, string expected)
    {
        Assert.Equal(Enum.Parse<DriverState>(expected), VirtualDisplayDriver.Evaluate(deviceInstalled, adapterPresent));
    }

    [Fact]
    public void RecognizesTheDriverByHardwareId()
    {
        Assert.True(VirtualDisplayDriver.IsDriverDevice(["Root\\MttVDD"]));
        Assert.True(VirtualDisplayDriver.IsDriverDevice(["ROOT\\OTRO", "root\\mttvdd"]));
        Assert.False(VirtualDisplayDriver.IsDriverDevice(["ROOT\\OTRO"]));
        Assert.False(VirtualDisplayDriver.IsDriverDevice([]));
    }
}
