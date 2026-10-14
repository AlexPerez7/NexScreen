using static NexScreen.Host.Display.NativeMethods;

namespace NexScreen.Host.Display;

internal sealed record DisplayInfo(
    string DeviceName,
    string AdapterName,
    string DeviceId,
    bool Attached,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    int RefreshRate)
{
    public override string ToString() => Attached
        ? $"{DeviceName} '{AdapterName}' {Width}x{Height}@{RefreshRate} en ({X},{Y}){(Primary ? " [principal]" : "")}"
        : $"{DeviceName} '{AdapterName}' (desconectado del escritorio)";
}

/// <summary>Enumeración de pantallas con la API de GDI (EnumDisplayDevices/EnumDisplaySettings).</summary>
internal static class DisplayDevices
{
    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        var device = DisplayDevice.Create();
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++, device = DisplayDevice.Create())
        {
            var attached = (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
            var mode = DevMode.Create();
            var hasMode = attached && EnumDisplaySettingsEx(device.DeviceName, EnumCurrentSettings, ref mode, 0);

            result.Add(new DisplayInfo(
                device.DeviceName,
                device.DeviceString,
                device.DeviceId,
                attached,
                (device.StateFlags & DisplayDevicePrimaryDevice) != 0,
                hasMode ? mode.PositionX : 0,
                hasMode ? mode.PositionY : 0,
                hasMode ? (int)mode.PelsWidth : 0,
                hasMode ? (int)mode.PelsHeight : 0,
                hasMode ? (int)mode.DisplayFrequency : 0));
        }

        return result;
    }

    /// <summary>Modos que el driver ofrece para una pantalla.</summary>
    public static IReadOnlyList<(int Width, int Height, int RefreshRate)> Modes(string deviceName)
    {
        var modes = new HashSet<(int, int, int)>();
        var mode = DevMode.Create();
        for (var i = 0; EnumDisplaySettingsEx(deviceName, i, ref mode, 0); i++, mode = DevMode.Create())
        {
            modes.Add(((int)mode.PelsWidth, (int)mode.PelsHeight, (int)mode.DisplayFrequency));
        }

        return modes.OrderByDescending(m => m.Item1 * m.Item2).ThenByDescending(m => m.Item3).ToList();
    }
}
