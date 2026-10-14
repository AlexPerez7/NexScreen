using Vortice.DXGI;

namespace NexScreen.Core.Display;

internal enum GpuVendor
{
    Unknown,
    Intel,
    Nvidia,
    Amd,
}

/// <summary>
/// Lo que ddagrab necesita para capturar una pantalla: el adaptador (la GPU a la que está
/// conectada, que también decide qué encoder de hardware sirve) y el índice de la salida dentro de él.
/// </summary>
internal readonly record struct CaptureTarget(int Adapter, int Output, GpuVendor Vendor);

internal sealed record DxgiOutput(CaptureTarget Target, string AdapterName, string DeviceName, Input.ScreenRect Bounds);

/// <summary>
/// Salidas DXGI de todas las GPU. En un PC con una sola GPU todo está en el adaptador 0, pero con
/// varias (un notebook con Intel y NVIDIA, o una de escritorio con la integrada activa) cada
/// pantalla cuelga de la GPU a la que está conectada.
/// </summary>
internal static class DxgiOutputs
{
    /// <summary>Todas las salidas, en el orden de los adaptadores (el índice global de --monitor).</summary>
    public static IReadOnlyList<DxgiOutput> List()
    {
        // Factory nueva en cada llamada: DXGI cachea la lista de salidas por factory.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var outputs = new List<DxgiOutput>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                // "Microsoft Basic Render Driver": no tiene pantallas ni encoder.
                if ((description.Flags & AdapterFlags.Software) != 0) continue;

                var vendor = VendorOf(description.VendorId);
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var rect = output.Description.DesktopCoordinates;
                        outputs.Add(new DxgiOutput(
                            new CaptureTarget((int)a, (int)o, vendor),
                            description.Description,
                            output.Description.DeviceName,
                            new Input.ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
                    }
                }
            }
        }

        return outputs;
    }

    public static GpuVendor VendorOf(uint vendorId) => vendorId switch
    {
        0x8086 => GpuVendor.Intel,
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        _ => GpuVendor.Unknown,
    };

    /// <summary>Salida por nombre GDI (\\.\DISPLAYn), o null si no está en el escritorio.</summary>
    public static DxgiOutput? Find(string deviceName) =>
        List().FirstOrDefault(o => string.Equals(o.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Salida por índice global (opción --monitor del modo espejo).</summary>
    public static CaptureTarget ByIndex(int index)
    {
        var outputs = List();
        return index < outputs.Count
            ? outputs[index].Target
            : throw new InvalidOperationException($"No existe el monitor {index}: hay {outputs.Count} (ver --list-displays)");
    }

    /// <summary>Rectángulo en el escritorio de la salida indicada.</summary>
    public static Input.ScreenRect Bounds(CaptureTarget target) =>
        List().FirstOrDefault(o => o.Target.Adapter == target.Adapter && o.Target.Output == target.Output)?.Bounds
        ?? throw new InvalidOperationException($"La salida {target.Output} del adaptador {target.Adapter} ya no existe");
}
