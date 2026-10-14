using System.Runtime.InteropServices;

namespace NexScreen.Host.Input;

/// <summary>P/Invoke de punteros sintéticos (InjectSyntheticPointerInput, Windows 10 1809+).</summary>
internal static class PointerNative
{
    public const uint PtTouch = 2;
    public const uint PtPen = 3;
    public const uint PointerFeedbackDefault = 1;

    public const uint FlagInRange = 0x00000002;
    public const uint FlagInContact = 0x00000004;
    public const uint FlagFirstButton = 0x00000010;
    public const uint FlagSecondButton = 0x00000020;
    public const uint FlagPrimary = 0x00002000;
    public const uint FlagConfidence = 0x00004000;
    public const uint FlagCanceled = 0x00008000;
    public const uint FlagDown = 0x00010000;
    public const uint FlagUpdate = 0x00020000;
    public const uint FlagUp = 0x00040000;

    public const uint TouchMaskContactArea = 0x00000001;
    public const uint TouchMaskOrientation = 0x00000002;
    public const uint TouchMaskPressure = 0x00000004;

    public const uint PenFlagBarrel = 0x00000001;
    public const uint PenFlagInverted = 0x00000002;
    public const uint PenFlagEraser = 0x00000004;

    public const uint PenMaskPressure = 0x00000001;
    public const uint PenMaskTiltX = 0x00000004;
    public const uint PenMaskTiltY = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>POINTER_INFO (96 bytes en x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public Point PtPixelLocation;
        public Point PtHimetricLocation;
        public Point PtPixelLocationRaw;
        public Point PtHimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    /// <summary>POINTER_TOUCH_INFO (144 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public Rect RcContact;
        public Rect RcContactRaw;
        public uint Orientation;
        public uint Pressure;
    }

    /// <summary>POINTER_PEN_INFO (120 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointerPenInfo
    {
        public PointerInfo PointerInfo;
        public uint PenFlags;
        public uint PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }

    /// <summary>POINTER_TYPE_INFO: tipo + unión táctil/lápiz alineada a 8 (152 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 152)]
    public struct PointerTypeInfo
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public PointerTouchInfo Touch;

        [FieldOffset(8)]
        public PointerPenInfo Pen;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectSyntheticPointerInput(IntPtr device, [In] PointerTypeInfo[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    public static extern void DestroySyntheticPointerDevice(IntPtr device);

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
