using NexScreen.Core.Video;
using NexScreen.Core.Video.Native;

namespace NexScreen.Core.Tests;

public class FramePacerTests
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(250);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void WaitsForTheFirstImage()
    {
        var pacer = new FramePacer(90, Heartbeat);

        var decision = pacer.Next(Ms(0), hasFrame: false, changed: false, keyFrameRequested: false, queueFull: false);

        Assert.False(decision.Send);
    }

    [Fact]
    public void SendsTheFirstImageImmediately()
    {
        var pacer = new FramePacer(90, Heartbeat);

        Assert.True(pacer.Next(Ms(5), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false).Send);
    }

    [Fact]
    public void WaitsForTheNextSlotWhenTheScreenChangesFasterThanTheFps()
    {
        var pacer = new FramePacer(90, Heartbeat); // 11,1 ms entre cuadros
        pacer.Sent(Ms(0));

        var early = pacer.Next(Ms(6.9), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false);

        Assert.False(early.Send);
        Assert.InRange(early.WaitMs, 1, 5);
        Assert.True(pacer.Next(Ms(11.2), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false).Send);
    }

    [Fact]
    public void KeepsTheFullRateWhenTheScreenChangesFasterThanTheFps()
    {
        // Pantalla a 144 Hz, 90 fps: cada cuadro sale en su turno de la rejilla, no cada dos llegadas.
        var pacer = new FramePacer(90, Heartbeat);
        var sent = 0;
        for (var ms = 0.0; ms < 1000; ms += 0.5)
        {
            if (!pacer.Next(Ms(ms), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false).Send) continue;
            pacer.Sent(Ms(ms));
            sent++;
        }

        Assert.InRange(sent, 88, 92);
    }

    [Fact]
    public void SendsAsSoonAsTheImageArrivesWhenTheScreenIsSlowerThanTheFps()
    {
        var pacer = new FramePacer(90, Heartbeat);
        pacer.Sent(Ms(0));

        // La pantalla va a 60 Hz: la próxima imagen llega a los 16,7 ms, ya pasó el turno.
        Assert.True(pacer.Next(Ms(16.7), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false).Send);
    }

    [Fact]
    public void DoesNotBurstToCatchUpAfterAStall()
    {
        var pacer = new FramePacer(90, Heartbeat);
        pacer.Sent(Ms(0));
        pacer.Sent(Ms(500)); // se atrasó medio segundo

        // El próximo turno es ahora + un intervalo, no uno pendiente desde hace rato.
        Assert.False(pacer.Next(Ms(502), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: false).Send);
    }

    [Fact]
    public void RepeatsTheLastFrameOnAnIdleScreenEveryHeartbeat()
    {
        var pacer = new FramePacer(90, Heartbeat);
        pacer.Sent(Ms(0));

        var idle = pacer.Next(Ms(100), hasFrame: true, changed: false, keyFrameRequested: false, queueFull: false);
        Assert.False(idle.Send);
        Assert.Equal(150, idle.WaitMs);
        Assert.True(pacer.Next(Ms(250), hasFrame: true, changed: false, keyFrameRequested: false, queueFull: false).Send);
    }

    [Fact]
    public void SendsAKeyFrameRequestOnAnIdleScreenAtTheNextSlot()
    {
        var pacer = new FramePacer(90, Heartbeat);
        pacer.Sent(Ms(0));

        Assert.True(pacer.Next(Ms(12), hasFrame: true, changed: false, keyFrameRequested: true, queueFull: false).Send);
    }

    [Fact]
    public void SkipsFramesWhileTheDestinationFallsBehind()
    {
        var pacer = new FramePacer(90, Heartbeat);

        var decision = pacer.Next(Ms(5), hasFrame: true, changed: true, keyFrameRequested: false, queueFull: true);

        Assert.False(decision.Send);
        Assert.Equal(1, decision.WaitMs);
    }
}

public class AnnexBTests
{
    private static readonly byte[] Sps = [0, 0, 0, 1, 0x67, 0x4D, 0x00, 0x1F];
    private static readonly byte[] Pps = [0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0x80];
    private static readonly byte[] Idr = [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00];
    private static readonly byte[] Slice = [0, 0, 1, 0x41, 0x9A, 0x24];

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void SplitsNalUnitsWithBothStartCodeLengths()
    {
        var nals = AnnexB.Split(Join(Sps, Pps, Slice));

        Assert.Equal([AnnexB.NalSps, AnnexB.NalPps, AnnexB.NalSlice], nals.Select(n => n.Type));
        Assert.Equal(0, nals[0].Start);
        Assert.Equal(Sps.Length, nals[1].Start);
        Assert.Equal(Sps.Length + Pps.Length, nals[2].Start); // el start code de 3 bytes
    }

    [Fact]
    public void DetectsAnIdrSlice()
    {
        Assert.True(AnnexB.ContainsIdr(Join(Sps, Pps, Idr)));
        Assert.False(AnnexB.ContainsIdr(Slice));
    }

    [Fact]
    public void ExtractsParameterSetsWithTheirStartCodes()
    {
        var parameters = AnnexB.ExtractParameterSets(Join(Sps, Pps, Idr));

        Assert.Equal(Join(Sps, Pps), parameters);
    }

    [Fact]
    public void ReturnsNullWithoutParameterSets()
    {
        Assert.Null(AnnexB.ExtractParameterSets(Join(Slice)));
        Assert.Empty(AnnexB.Split([]));
    }
}

public class CursorImageTests
{
    [Fact]
    public void ColorShapesKeepTheirPixelsAndDropRowPadding()
    {
        // 2x1 píxeles con 4 bytes de relleno por fila.
        byte[] shape = [1, 2, 3, 255, 4, 5, 6, 128, 0, 0, 0, 0];

        var (width, height, bgra) = CursorImage.ToBgra(CursorImage.Color, 2, 1, 12, shape);

        Assert.Equal((2, 1), (width, height));
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 128], bgra);
    }

    [Fact]
    public void MaskedColorShapesMakeAlphaZeroOpaqueAndAlpha255Transparent()
    {
        byte[] shape = [10, 20, 30, 0, 40, 50, 60, 255];

        var (_, _, bgra) = CursorImage.ToBgra(CursorImage.MaskedColor, 2, 1, 8, shape);

        Assert.Equal(255, bgra[3]);
        Assert.Equal(0, bgra[7]);
        Assert.Equal([10, 20, 30], bgra[..3]);
    }

    [Fact]
    public void MonochromeShapesCombineTheAndAndXorMasks()
    {
        // 4x1 (la forma mide 2 filas: AND arriba, XOR abajo). Un byte por fila, bits de izquierda a derecha.
        // x=0: AND0 XOR0 negro opaco; x=1: AND0 XOR1 blanco opaco; x=2: AND1 XOR0 transparente; x=3: AND1 XOR1 blanco.
        byte[] shape = [0b0011_0000, 0b0101_0000];

        var (width, height, bgra) = CursorImage.ToBgra(CursorImage.Monochrome, 4, 2, 1, shape);

        Assert.Equal((4, 1), (width, height));
        Assert.Equal([0, 0, 0, 255], bgra[0..4]);
        Assert.Equal([255, 255, 255, 255], bgra[4..8]);
        Assert.Equal(0, bgra[11]);
        Assert.Equal([255, 255, 255, 255], bgra[12..16]);
    }

    [Fact]
    public void RejectsUnknownShapeTypes()
    {
        Assert.Throws<NotSupportedException>(() => CursorImage.ToBgra(9, 1, 1, 4, new byte[4]));
    }
}

public class FitSizeTests
{
    [Theory]
    [InlineData(2304, 1440, 2304, 1440, 2304, 1440)] // Entra: no se toca.
    [InlineData(3840, 2160, 2304, 1440, 2304, 1296)] // Pantalla 4K en la tablet: manda el ancho.
    [InlineData(1920, 1080, 2304, 1440, 1920, 1080)] // Más chica que el dispositivo: no se agranda.
    [InlineData(2560, 1600, 1280, 720, 1152, 720)] // Manda el alto.
    [InlineData(1366, 768, 1280, 800, 1280, 718)] // Los lados salen pares.
    public void FitsWithoutEnlargingAndWithEvenSides(int width, int height, int maxWidth, int maxHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), StreamTuning.FitSize(width, height, maxWidth, maxHeight));
    }
}
