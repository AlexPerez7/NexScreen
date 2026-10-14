using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using NexScreen.Host;
using NexScreen.Host.Display;
using NexScreen.Host.Net;
using NexScreen.Host.Protocol;

Console.OutputEncoding = Encoding.UTF8;
// Coordenadas y resoluciones de pantalla en píxeles físicos, sin virtualización de DPI.
NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessPerMonitorV2);

HostOptions options;
try
{
    options = HostOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(HostOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(HostOptions.Usage);
    return 0;
}

if (options.ListDisplays)
{
    PrintDisplays();
    return 0;
}

Log.Verbose = options.Verbose;
var version = Assembly.GetExecutingAssembly().GetName().Version;
Log.Info($"NexScreen host {version?.ToString(3)} (protocolo v{Wire.Version})");

if (!FfmpegAvailable(options.FfmpegPath))
{
    Log.Error($"No se encontró ffmpeg ('{options.FfmpegPath}'). Instalalo con: winget install Gyan.FFmpeg");
    return 1;
}

options.ResolveMode(VirtualDisplay.Find() is not null);
if (options.Mode == ScreenMode.Extend)
{
    if (VirtualDisplay.Find() is null)
    {
        Log.Error("--mode extend requiere el monitor virtual: ejecutá scripts\\install-virtual-display.ps1 como administrador");
        return 1;
    }

    Log.Info(options.Position is { } position
        ? $"Modo extendido: la tablet será un monitor a la {PositionName(position)}"
        : "Modo extendido: la tablet será un monitor en la última posición usada (a la derecha la primera vez)");
    // Si el host terminó mal en una sesión anterior, el monitor virtual pudo quedar en el escritorio.
    VirtualDisplay.Detach(rememberPosition: false);
}
else
{
    Log.Info($"Modo espejo: se captura el monitor {options.Monitor}");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Info("Deteniendo…");
    cts.Cancel();
};

await new StreamServer(options).RunAsync(cts.Token);
return 0;

static string PositionName(DisplayPosition position) => position switch
{
    DisplayPosition.Right => "derecha",
    DisplayPosition.Left => "izquierda",
    DisplayPosition.Above => "arriba",
    _ => "abajo",
};

static void PrintDisplays()
{
    Console.WriteLine("Pantallas (GDI):");
    foreach (var display in DisplayDevices.List())
    {
        Console.WriteLine($"  {display}{(VirtualDisplay.IsVirtual(display) ? "  <- monitor virtual NexScreen" : "")}");
        if (VirtualDisplay.IsVirtual(display))
        {
            var modes = DisplayDevices.Modes(display.DeviceName).Select(m => $"{m.Width}x{m.Height}@{m.RefreshRate}");
            Console.WriteLine($"      modos: {string.Join(", ", modes)}");
        }
    }

    Console.WriteLine("Caminos CCD (fuente -> monitor):");
    foreach (var path in DisplayConfig.DescribePaths()) Console.WriteLine($"  {path}");

    Console.WriteLine("Salidas DXGI del adaptador 0 (índice para --monitor):");
    var outputs = DxgiOutputs.ListDefaultAdapterOutputs();
    for (var i = 0; i < outputs.Count; i++) Console.WriteLine($"  {i}: {outputs[i]}");
}

static bool FfmpegAvailable(string path)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo(path, "-version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null) return false;
        process.WaitForExit(5000);
        return process.HasExited && process.ExitCode == 0;
    }
    catch (Win32Exception)
    {
        return false;
    }
}
