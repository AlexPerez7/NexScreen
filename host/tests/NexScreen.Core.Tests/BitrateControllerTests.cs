using NexScreen.Core.Video;

namespace NexScreen.Core.Tests;

public class BitrateControllerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void StartsAtMaxAndComputesFloor()
    {
        var controller = new BitrateController(12);

        Assert.Equal(12, controller.CurrentMbps);
        Assert.Equal(3, controller.MinMbps);
        Assert.Equal(2, new BitrateController(6).MinMbps);
        Assert.Equal(1, new BitrateController(1).MinMbps);
    }

    [Fact]
    public void ClearNetworkAtMaxNeverChanges()
    {
        var controller = new BitrateController(12);

        for (var t = 1; t <= 120; t++) Assert.Null(controller.OnWindow(0.0, 11, S(t)));
        Assert.Equal(12, controller.CurrentMbps);
    }

    [Fact]
    public void SevereCongestionDecreasesAtOnceTowardsThroughput()
    {
        var controller = new BitrateController(12);

        // Se enviaron 6 Mbps con la red saturada: apuntar al 85 % de eso, no solo al 70 % de 12.
        Assert.Equal(5, controller.OnWindow(0.8, 6, S(1)));
        Assert.Equal(5, controller.CurrentMbps);
    }

    [Fact]
    public void MildCongestionNeedsTwoWindowsInARow()
    {
        var controller = new BitrateController(12);

        Assert.Null(controller.OnWindow(0.3, 11, S(1)));
        Assert.Null(controller.OnWindow(0.0, 11, S(2)));
        Assert.Null(controller.OnWindow(0.3, 11, S(3)));
        Assert.Equal(8, controller.OnWindow(0.3, 11, S(4))); // 12 × 0.7
    }

    [Fact]
    public void IgnoresWindowsDuringCooldown()
    {
        var controller = new BitrateController(12);
        Assert.Equal(8, controller.OnWindow(0.9, 11, S(1)));

        Assert.Null(controller.OnWindow(0.9, 7, S(2)));
        Assert.Null(controller.OnWindow(0.9, 7, S(3.5)));
        Assert.Equal(5, controller.OnWindow(0.9, 7, S(4))); // min(8 × 0.7, 7 × 0.85)
    }

    [Fact]
    public void NeverGoesBelowFloor()
    {
        var controller = new BitrateController(12);
        var t = 0;
        for (var i = 0; i < 10; i++) controller.OnWindow(0.9, 0.5, S(t += 5));

        Assert.Equal(3, controller.CurrentMbps);
        Assert.Null(controller.OnWindow(0.9, 0.5, S(t + 5)));
    }

    [Fact]
    public void IncreasesAfterHoldOnlyWithRealLoad()
    {
        var controller = new BitrateController(12);
        Assert.Equal(8, controller.OnWindow(0.9, 11, S(0)));

        // Pantalla quieta: se envía poco, no prueba nada.
        for (var t = 4; t < 40; t++) Assert.Null(controller.OnWindow(0.0, 0.5, S(t)));

        // Con carga (≥ 40 % de 8 Mbps), 10 segundos holgados suben un paso (12 / 6 = 2).
        for (var t = 40; t < 49; t++) Assert.Null(controller.OnWindow(0.0, 6, S(t)));
        Assert.Equal(10, controller.OnWindow(0.0, 6, S(49)));
    }

    [Fact]
    public void FailedIncreaseDoublesTheHold()
    {
        var controller = new BitrateController(12);
        Assert.Equal(8, controller.OnWindow(0.9, 11, S(0)));
        var t = 3;
        for (var i = 0; i < 10; i++) controller.OnWindow(0.0, 7, S(++t));
        Assert.Equal(10, controller.CurrentMbps);

        // La subida congestiona enseguida: baja, y la próxima subida necesita 20 segundos.
        Assert.Equal(7, controller.OnWindow(0.9, 9, S(t += 4)));
        t += 3;
        for (var i = 0; i < 19; i++) Assert.Null(controller.OnWindow(0.0, 6, S(++t)));
        Assert.Equal(9, controller.OnWindow(0.0, 6, S(++t)));
    }

    [Fact]
    public void SuccessfulIncreaseResetsTheHold()
    {
        var controller = new BitrateController(12);
        Assert.Equal(8, controller.OnWindow(0.9, 11, S(0)));
        for (var t = 4; t < 13; t++) controller.OnWindow(0.0, 7, S(t));
        Assert.Equal(10, controller.OnWindow(0.0, 7, S(13)));

        // Subida fallida: la espera pasa a 20 segundos.
        Assert.Equal(7, controller.OnWindow(0.9, 9, S(17)));
        for (var t = 20; t < 39; t++) Assert.Null(controller.OnWindow(0.0, 6, S(t)));
        Assert.Equal(9, controller.OnWindow(0.0, 6, S(39)));

        // Esta subida aguanta los 15 s de prueba: la espera vuelve a 10 y sube en t = 54, no en t = 61.
        for (var t = 42; t < 54; t++) Assert.Null(controller.OnWindow(0.0, 6, S(t)));
        Assert.Equal(11, controller.OnWindow(0.0, 6, S(54)));
    }

    [Fact]
    public void MildBlockingNeitherIncreasesNorResets()
    {
        var controller = new BitrateController(12);
        Assert.Equal(8, controller.OnWindow(0.9, 11, S(0)));
        var t = 3;
        for (var i = 0; i < 5; i++) controller.OnWindow(0.0, 7, S(++t));
        for (var i = 0; i < 5; i++) Assert.Null(controller.OnWindow(0.1, 7, S(++t)));
        for (var i = 0; i < 4; i++) Assert.Null(controller.OnWindow(0.0, 7, S(++t)));
        Assert.Equal(10, controller.OnWindow(0.0, 7, S(++t)));
    }
}
