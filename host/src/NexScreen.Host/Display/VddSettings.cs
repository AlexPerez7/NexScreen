using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

namespace NexScreen.Host.Display;

/// <summary>
/// Configuración y control del Virtual Display Driver
/// (https://github.com/VirtualDrivers/Virtual-Display-Driver).
/// </summary>
internal static class VddSettings
{
    public const string DefaultPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    private const string PipeName = "MTTVirtualDisplayPipe";

    /// <summary>
    /// Agrega la resolución como preferida (primera de la lista) si todavía no está.
    /// Devuelve true si el archivo cambió y hay que recargar el driver.
    /// </summary>
    public static bool EnsureResolution(int width, int height, int refreshRate, string path = DefaultPath)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var resolutions = document.Root?.Element("resolutions")
            ?? throw new InvalidDataException($"{path} no tiene <resolutions>");

        var exists = resolutions.Elements("resolution").Any(r =>
            (int?)r.Element("width") == width
            && (int?)r.Element("height") == height
            && (int?)r.Element("refresh_rate") == refreshRate);
        if (exists) return false;

        resolutions.AddFirst(new XElement("resolution",
            new XElement("width", width),
            new XElement("height", height),
            new XElement("refresh_rate", refreshRate)));
        document.Save(path);
        return true;
    }

    /// <summary>
    /// Envía un comando al driver por su named pipe (abierto a todos los usuarios, no requiere admin).
    /// Comandos útiles: RELOAD_DRIVER, PING. Ver HandleClient en Driver.cpp.
    /// </summary>
    public static async Task SendCommandAsync(string command, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
        var bytes = Encoding.Unicode.GetBytes(command);
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }
}
