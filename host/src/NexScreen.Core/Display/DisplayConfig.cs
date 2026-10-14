using System.Runtime.InteropServices;

namespace NexScreen.Core.Display;

/// <summary>
/// API CCD (Connecting and Configuring Displays): la única forma confiable en Windows moderno de
/// agregar o quitar una pantalla del escritorio. ChangeDisplaySettingsEx no puede activar una
/// pantalla que está desconectada del escritorio.
/// </summary>
internal static class DisplayConfig
{
    private const uint QdcAllPaths = 0x00000001;
    private const uint QdcOnlyActivePaths = 0x00000002;

    private const uint SdcApply = 0x00000080;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcSaveToDatabase = 0x00000200;
    private const uint SdcAllowChanges = 0x00000400;

    private const uint PathActive = 0x00000001;
    private const uint ModeIdxInvalid = 0xFFFFFFFF;
    private const int DeviceInfoGetSourceName = 1;
    private const int DeviceInfoGetTargetName = 2;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// Monitor del Virtual Display Driver. IddCx lo expone como un target más del adaptador que
    /// renderiza (la GPU), así que se identifica por su EDID (fabricante "MTT") y no por nombre GDI.
    /// </summary>
    private static bool IsVirtualTarget(PathInfo path)
    {
        var (monitor, devicePath) = TargetName(path);
        return devicePath.Contains("#MTT1337#", StringComparison.OrdinalIgnoreCase)
               || monitor.Equals("VDD by MTT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pone el monitor virtual como pantalla extendida con una fuente propia (si estaba duplicando
    /// otra pantalla, deja de hacerlo). Las demás pantallas activas quedan como están.
    /// Devuelve el nombre GDI (\\.\DISPLAYn) con el que quedó en el escritorio.
    /// </summary>
    public static string AttachVirtual()
    {
        var (paths, modes) = Query(QdcAllPaths);
        var active = paths.Where(p => (p.Flags & PathActive) != 0).ToList();

        var current = active.Where(IsVirtualTarget).ToList();
        if (current.Count == 1 && !active.Any(p => !IsVirtualTarget(p) && SameSource(p, current[0])))
        {
            return SourceName(current[0]) ?? throw new InvalidOperationException("Monitor virtual sin nombre GDI");
        }

        var others = active.Where(p => !IsVirtualTarget(p)).ToList();
        var candidate = paths
            .Where(p => p.TargetInfo.TargetAvailable != 0 && IsVirtualTarget(p))
            .Where(p => !others.Any(o => SameSource(o, p)))
            .Cast<PathInfo?>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("El monitor virtual no tiene una fuente libre en la GPU");

        var path = candidate;
        path.Flags = PathActive;
        // Sin modo: Windows elige uno y después se ajusta con ChangeDisplaySettingsEx.
        path.SourceInfo.ModeInfoIdx = ModeIdxInvalid;
        path.TargetInfo.ModeInfoIdx = ModeIdxInvalid;
        others.Add(path);

        Set(others, modes, "agregar");
        return SourceName(path) ?? throw new InvalidOperationException("Monitor virtual sin nombre GDI");
    }

    /// <summary>Nombre GDI con el que el monitor virtual está en el escritorio, o null si no está.</summary>
    public static string? VirtualSourceName()
    {
        var (paths, _) = Query(QdcOnlyActivePaths);
        return paths.Where(IsVirtualTarget).Select(SourceName).FirstOrDefault(n => n is not null);
    }

    /// <summary>Quita el monitor virtual del escritorio. Devuelve false si no estaba activo.</summary>
    public static bool DetachVirtual()
    {
        var (paths, modes) = Query(QdcOnlyActivePaths);
        var remaining = paths.Where(p => !IsVirtualTarget(p)).ToList();
        if (remaining.Count == paths.Length) return false;
        if (remaining.Count == 0) throw new InvalidOperationException("No se puede desactivar la única pantalla activa");

        Set(remaining, modes, "quitar");
        return true;
    }

    /// <summary>Diagnóstico: todos los caminos fuente→monitor que conoce Windows.</summary>
    public static IEnumerable<string> DescribePaths()
    {
        var (paths, _) = Query(QdcAllPaths);
        foreach (var path in paths)
        {
            var (monitor, devicePath) = TargetName(path);
            yield return $"{((path.Flags & PathActive) != 0 ? "ACTIVO" : "      ")} " +
                         $"fuente {path.SourceInfo.AdapterId.HighPart:X}:{path.SourceInfo.AdapterId.LowPart:X}/{path.SourceInfo.Id} {SourceName(path) ?? "?"} -> " +
                         $"target {path.TargetInfo.AdapterId.HighPart:X}:{path.TargetInfo.AdapterId.LowPart:X}/{path.TargetInfo.Id} " +
                         $"disponible={path.TargetInfo.TargetAvailable != 0} '{monitor}' {devicePath}";
        }
    }

    private static (string Monitor, string DevicePath) TargetName(PathInfo path)
    {
        var request = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DeviceInfoGetTargetName,
                Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0
            ? (request.MonitorFriendlyDeviceName, request.MonitorDevicePath)
            : ("?", "");
    }

    private static bool SameSource(PathInfo a, PathInfo b) =>
        a.SourceInfo.AdapterId.Equals(b.SourceInfo.AdapterId) && a.SourceInfo.Id == b.SourceInfo.Id;

    private static (PathInfo[] Paths, ModeInfo[] Modes) Query(uint flags)
    {
        while (true)
        {
            var error = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes devolvió {error}");

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            error = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            // La configuración puede cambiar entre las dos llamadas: reintentar.
            if (error == ErrorInsufficientBuffer) continue;
            if (error != 0) throw new InvalidOperationException($"QueryDisplayConfig devolvió {error}");

            return (paths[..(int)pathCount], modes[..(int)modeCount]);
        }
    }

    private static void Set(List<PathInfo> paths, ModeInfo[] modes, string action)
    {
        var (compactPaths, compactModes) = Compact(paths, modes);
        var error = SetDisplayConfig(
            (uint)compactPaths.Length, compactPaths, (uint)compactModes.Length, compactModes,
            SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
        if (error != 0) throw new InvalidOperationException($"No se pudo {action} la pantalla (SetDisplayConfig devolvió {error})");
    }

    /// <summary>Deja solo los modos referenciados por los caminos y reindexa las referencias.</summary>
    private static (PathInfo[] Paths, ModeInfo[] Modes) Compact(List<PathInfo> paths, ModeInfo[] modes)
    {
        var compactModes = new List<ModeInfo>();
        var remap = new Dictionary<uint, uint>();

        uint Remap(uint index)
        {
            if (index == ModeIdxInvalid || index >= modes.Length) return ModeIdxInvalid;
            if (!remap.TryGetValue(index, out var newIndex))
            {
                newIndex = (uint)compactModes.Count;
                compactModes.Add(modes[index]);
                remap[index] = newIndex;
            }

            return newIndex;
        }

        var result = paths.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            result[i].SourceInfo.ModeInfoIdx = Remap(result[i].SourceInfo.ModeInfoIdx);
            result[i].TargetInfo.ModeInfoIdx = Remap(result[i].TargetInfo.ModeInfoIdx);
        }

        return (result, compactModes.ToArray());
    }

    private static string? SourceName(PathInfo path)
    {
        var request = new SourceDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DeviceInfoGetSourceName,
                Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            },
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.ViewGdiDeviceName : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO (64 bytes). La unión de 48 bytes del final no se interpreta,
    /// solo se reenvía tal cual a SetDisplayConfig.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] PathInfo[] paths, uint modeCount, [In] ModeInfo[] modes, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
}
