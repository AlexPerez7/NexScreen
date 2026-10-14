using NexScreen.Core.Display;

namespace NexScreen.Core.Tests;

public class DpiScaleTests
{
    // Monitor cuya escala recomendada es 150 %: min = -2 (100 %), max = +6 (350 %).
    private const int Min = -2;
    private const int Max = 6;

    [Theory]
    [InlineData(-2, 100)]
    [InlineData(0, 150)]
    [InlineData(2, 200)]
    [InlineData(5, 300)]
    [InlineData(6, 350)]
    public void ConvertsRelativeToPercent(int relative, int percent)
    {
        Assert.Equal(percent, DpiScale.ToPercent(Min, relative));
    }

    [Theory]
    [InlineData(100, -2)]
    [InlineData(150, 0)]
    [InlineData(175, 1)]
    [InlineData(300, 5)]
    public void ConvertsPercentToRelative(int percent, int relative)
    {
        Assert.Equal(relative, DpiScale.ToRelative(Min, Max, percent));
    }

    [Fact]
    public void RoundsToNearestStep()
    {
        Assert.Equal(1, DpiScale.ToRelative(Min, Max, 180)); // 175 %
    }

    [Fact]
    public void ClampsToWhatTheMonitorSupports()
    {
        Assert.Equal(Max, DpiScale.ToRelative(Min, Max, 500));
        Assert.Equal(Min, DpiScale.ToRelative(Min, Max, 50));
    }

    [Fact]
    public void RoundTripsWhenRecommendedIs100()
    {
        // Recomendada 100 %: min = 0.
        Assert.Equal(2, DpiScale.ToRelative(0, 4, 150));
        Assert.Equal(150, DpiScale.ToPercent(0, 2));
    }
}
