using NexScreen.Core.Display;

namespace NexScreen.Core.Tests;

public class PlacementTests
{
    private static readonly DisplayInfo Laptop =
        new(@"\\.\DISPLAY1", "Intel(R) UHD Graphics", "PCI\\VEN_8086", true, true, 0, 0, 1920, 1200, 60);

    [Theory]
    [InlineData(-2304, 0, true)]      // a la izquierda, tocando el borde
    [InlineData(1920, -300, true)]    // a la derecha, desplazada hacia arriba pero tocando
    [InlineData(0, 1200, true)]       // abajo
    [InlineData(-2304, 1200, false)]  // solo toca una esquina
    [InlineData(1000, 0, false)]      // se superpone
    [InlineData(3000, 0, false)]      // separada
    public void ValidatesPlacement(int x, int y, bool expected)
    {
        Assert.Equal(expected, VirtualDisplay.IsValidPlacement([Laptop], x, y, 2304, 1440));
    }

    [Fact]
    public void UsesRememberedPositionWhenNoneRequested()
    {
        var state = new HostState { VirtualDisplayX = -2304, VirtualDisplayY = 0 };

        Assert.Equal((-2304, 0), VirtualDisplay.ChoosePosition([Laptop], 2304, 1440, null, state));
    }

    [Fact]
    public void ExplicitPositionWinsOverRememberedOne()
    {
        var state = new HostState { VirtualDisplayX = -2304, VirtualDisplayY = 0 };

        Assert.Equal((1920, 0), VirtualDisplay.ChoosePosition([Laptop], 2304, 1440, DisplayPosition.Right, state));
    }

    [Fact]
    public void IgnoresRememberedPositionThatNoLongerFits()
    {
        var state = new HostState { VirtualDisplayX = 2560, VirtualDisplayY = 0 }; // había otro monitor en el medio

        Assert.Equal((1920, 0), VirtualDisplay.ChoosePosition([Laptop], 2304, 1440, null, state));
    }

    [Fact]
    public void KeepsTheRememberedSideForALargerDevice()
    {
        // La tablet (2304 de ancho) quedó a la izquierda; un celular de 3120 no entra en ese lugar.
        var state = new HostState { VirtualDisplayX = -2304, VirtualDisplayY = 0 };

        Assert.Equal((-3120, 0), VirtualDisplay.ChoosePosition([Laptop], 3120, 1440, null, state));
    }

    [Theory]
    [InlineData(-2304, 0, "Left")]
    [InlineData(1920, 0, "Right")]
    [InlineData(0, -1440, "Above")]
    [InlineData(0, 1200, "Below")]
    public void InfersSideOfRememberedPosition(int x, int y, string expected)
    {
        Assert.Equal(Enum.Parse<DisplayPosition>(expected), VirtualDisplay.SideOf([Laptop], x, y));
    }

    [Fact]
    public void StateRoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"host-state-{Guid.NewGuid():N}.json");
        try
        {
            new HostState { VirtualDisplayX = -2304, VirtualDisplayY = 120 }.Save(path);

            var loaded = HostState.Load(path);

            Assert.Equal(-2304, loaded.VirtualDisplayX);
            Assert.Equal(120, loaded.VirtualDisplayY);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
