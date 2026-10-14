using System.Runtime.InteropServices;
using NexScreen.Core.Input;
using static NexScreen.Core.Input.PointerNative;

namespace NexScreen.Core.Tests;

public class PointerNativeLayoutTests
{
    [Fact]
    public void StructSizesMatchWindowsX64()
    {
        Assert.Equal(96, Marshal.SizeOf<PointerInfo>());
        Assert.Equal(144, Marshal.SizeOf<PointerTouchInfo>());
        Assert.Equal(120, Marshal.SizeOf<PointerPenInfo>());
        Assert.Equal(152, Marshal.SizeOf<PointerTypeInfo>());
    }

    [Fact]
    public void FieldOffsetsMatchWindowsX64()
    {
        Assert.Equal(16, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.SourceDevice)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PtPixelLocation)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PerformanceCount)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.TouchFlags)).ToInt32());
        Assert.Equal(104, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.RcContact)).ToInt32());
        Assert.Equal(104, Marshal.OffsetOf<PointerPenInfo>(nameof(PointerPenInfo.Pressure)).ToInt32());
    }
}

public class PointerMessageTests
{
    [Fact]
    public void RoundTripsSamples()
    {
        PointerSample[] samples =
        [
            new(3, PointerTool.Finger, PointerAction.Down, false, 0.25, 0.75, 0.5, 0, 0),
            new(0, PointerTool.Pen, PointerAction.Move, true, 1, 0, 1, -45, 30),
        ];

        var parsed = PointerMessage.Parse(PointerMessage.Write(samples));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(PointerTool.Finger, parsed[0].Tool);
        Assert.Equal(3, parsed[0].Id);
        Assert.Equal(0.25, parsed[0].X, 4);
        Assert.Equal(0.75, parsed[0].Y, 4);
        Assert.True(parsed[1].BarrelButton);
        Assert.Equal(-45, parsed[1].TiltX);
        Assert.Equal(30, parsed[1].TiltY);
        Assert.Equal(1, parsed[1].Pressure, 4);
    }

    [Fact]
    public void RejectsWrongLength()
    {
        Assert.Throws<InvalidDataException>(() => PointerMessage.Parse([2, 0, 0, 0]));
    }

    [Fact]
    public void RejectsUnknownAction()
    {
        var payload = PointerMessage.Write([new(0, PointerTool.Finger, PointerAction.Down, false, 0, 0, 0, 0, 0)]);
        payload[3] = 9;

        Assert.Throws<InvalidDataException>(() => PointerMessage.Parse(payload));
    }
}

public class PointerTranslatorTests
{
    private static readonly ScreenRect Screen = new(-2304, 0, 2304, 1440);

    private static PointerSample Finger(byte id, PointerAction action, double x = 0.5, double y = 0.5) =>
        new(id, PointerTool.Finger, action, false, x, y, 1, 0, 0);

    private static PointerSample Pen(PointerAction action, double pressure = 0.5, bool barrel = false, PointerTool tool = PointerTool.Pen) =>
        new(0, tool, action, barrel, 0.5, 0.5, pressure, 10, -20);

    [Fact]
    public void MapsNormalizedCoordinatesToVirtualMonitor()
    {
        Assert.Equal(-2304, Screen.ToPixel(0, 0).X);
        Assert.Equal(-1, Screen.ToPixel(1, 1).X);
        Assert.Equal(1439, Screen.ToPixel(1, 1).Y);
    }

    [Fact]
    public void TouchTapProducesDownThenUp()
    {
        var translator = new PointerTranslator();

        var down = translator.TouchFrame([Finger(5, PointerAction.Down)], Screen);
        var up = translator.TouchFrame([Finger(5, PointerAction.Up)], Screen);

        Assert.Single(down);
        Assert.True(Has(down[0].Touch.PointerInfo.PointerFlags, FlagDown | FlagInContact | FlagPrimary));
        Assert.Equal(0u, down[0].Touch.PointerInfo.PointerId);
        Assert.Equal(-1152, down[0].Touch.PointerInfo.PtPixelLocation.X);
        Assert.True(Has(up[0].Touch.PointerInfo.PointerFlags, FlagUp));
        Assert.False(translator.HasActiveTouches);
    }

    [Fact]
    public void EveryFrameIncludesAllActiveContacts()
    {
        var translator = new PointerTranslator();
        translator.TouchFrame([Finger(1, PointerAction.Down)], Screen);
        translator.TouchFrame([Finger(1, PointerAction.Move), Finger(2, PointerAction.Down)], Screen);

        // Solo se menciona el dedo 2; el 1 sigue apoyado y tiene que viajar en el frame.
        var frame = translator.TouchFrame([Finger(2, PointerAction.Move, 0.6)], Screen);

        Assert.Equal(2, frame.Length);
        Assert.Contains(frame, f => f.Touch.PointerInfo.PointerId == 0 && Has(f.Touch.PointerInfo.PointerFlags, FlagUpdate | FlagInContact));
        Assert.Contains(frame, f => f.Touch.PointerInfo.PointerId == 1);
    }

    [Fact]
    public void MoveWithoutDownStartsContact()
    {
        var frame = new PointerTranslator().TouchFrame([Finger(7, PointerAction.Move)], Screen);

        Assert.True(Has(frame[0].Touch.PointerInfo.PointerFlags, FlagDown));
    }

    [Fact]
    public void UpForUnknownContactIsIgnored()
    {
        Assert.Empty(new PointerTranslator().TouchFrame([Finger(7, PointerAction.Up)], Screen));
    }

    [Fact]
    public void ReleaseAllLiftsStuckFingers()
    {
        var translator = new PointerTranslator();
        translator.TouchFrame([Finger(1, PointerAction.Down), Finger(2, PointerAction.Down)], Screen);

        var release = translator.ReleaseAllTouches();

        Assert.Equal(2, release.Length);
        Assert.All(release, f => Assert.True(Has(f.Touch.PointerInfo.PointerFlags, FlagUp | FlagCanceled)));
    }

    [Fact]
    public void PenGoesFromHoverToContactAndBack()
    {
        var translator = new PointerTranslator();

        var hover = translator.PenFrames(Pen(PointerAction.Hover), Screen).Single();
        var down = translator.PenFrames(Pen(PointerAction.Down, 0.5), Screen).Single();
        var move = translator.PenFrames(Pen(PointerAction.Move, 1), Screen).Single();
        var up = translator.PenFrames(Pen(PointerAction.Up), Screen).Single();
        var exit = translator.PenFrames(Pen(PointerAction.HoverExit), Screen).Single();

        Assert.Equal(FlagUpdate | FlagInRange, hover.Pen.PointerInfo.PointerFlags & ~FlagPrimary);
        Assert.True(Has(down.Pen.PointerInfo.PointerFlags, FlagDown | FlagInContact | FlagFirstButton));
        Assert.Equal(512u, down.Pen.Pressure);
        Assert.True(Has(move.Pen.PointerInfo.PointerFlags, FlagUpdate | FlagInContact));
        Assert.Equal(1024u, move.Pen.Pressure);
        Assert.Equal(10, move.Pen.TiltX);
        Assert.Equal(-20, move.Pen.TiltY);
        Assert.Equal(FlagUp | FlagInRange, up.Pen.PointerInfo.PointerFlags & ~FlagPrimary);
        Assert.Equal(FlagUpdate, exit.Pen.PointerInfo.PointerFlags & ~FlagPrimary);
    }

    [Fact]
    public void HoverExitWhileTouchingClosesContactFirst()
    {
        var translator = new PointerTranslator();
        translator.PenFrames(Pen(PointerAction.Down), Screen);

        var frames = translator.PenFrames(Pen(PointerAction.HoverExit), Screen);

        Assert.Equal(2, frames.Count);
        Assert.True(Has(frames[0].Pen.PointerInfo.PointerFlags, FlagUp));
    }

    [Fact]
    public void BarrelButtonAndEraserSetPenFlags()
    {
        var translator = new PointerTranslator();

        var barrel = translator.PenFrames(Pen(PointerAction.Down, barrel: true), Screen).Single();
        translator.PenFrames(Pen(PointerAction.Up), Screen);
        var eraser = translator.PenFrames(Pen(PointerAction.Down, tool: PointerTool.Eraser), Screen).Single();

        Assert.True(Has(barrel.Pen.PenFlags, PenFlagBarrel));
        Assert.True(Has(barrel.Pen.PointerInfo.PointerFlags, FlagSecondButton));
        Assert.True(Has(eraser.Pen.PenFlags, PenFlagEraser | PenFlagInverted));
    }

    private static bool Has(uint flags, uint expected) => (flags & expected) == expected;
}
