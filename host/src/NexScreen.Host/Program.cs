using System.Reflection;
using System.Text;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Net;
using NexScreen.Core.Protocol;

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

if (HostStartup.Prepare(options) is { } error)
{
    Log.Error(error);
    return 1;
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
