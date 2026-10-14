using Vortice.DXGI;

namespace NexScreen.Core.Display;

/// <summary>
/// ffmpeg ddagrab captura por índice de salida DXGI del adaptador por defecto (el 0).
/// Esta clase traduce un nombre GDI (\\.\DISPLAYn) a ese índice.
/// </summary>
internal static class DxgiOutputs
{
    public static IReadOnlyList<string> ListDefaultAdapterOutputs()
    {
        // Factory nueva en cada llamada: DXGI cachea la lista de salidas por factory.
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1(0, out var adapter).Failure) return [];

        using (adapter)
        {
            var outputs = new List<string>();
            for (uint i = 0; adapter.EnumOutputs(i, out var output).Success; i++)
            {
                using (output) outputs.Add(output.Description.DeviceName);
            }

            return outputs;
        }
    }

    /// <summary>Rectángulo en el escritorio de la salida DXGI indicada.</summary>
    public static Input.ScreenRect Bounds(int index)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out var adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs((uint)index, out var output).CheckError();
            using (output)
            {
                var rect = output.Description.DesktopCoordinates;
                return new Input.ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
        }
    }

    public static int? FindIndex(string deviceName)
    {
        var outputs = ListDefaultAdapterOutputs();
        for (var i = 0; i < outputs.Count; i++)
        {
            if (string.Equals(outputs[i], deviceName, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return null;
    }
}
