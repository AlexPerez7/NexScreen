using System.Reflection;
using System.Text;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Net;
using NexScreen.Core.Protocol;
using NexScreen.Core.Security;
using NexScreen.Core.Usb;

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

var server = new StreamServer(options, new ConsoleStreamObserver());
var tasks = new List<Task>
{
    server.RunAsync(cts.Token),
    new DiscoveryResponder(options.Port, version?.ToString(3) ?? "0.0.0").RunAsync(cts.Token),
};
var adbPath = ToolLocator.Find("adb");
if (options.UsbAutoConnect && ToolLocator.Runs(adbPath, "version"))
{
    var usb = new UsbAutoConnector(
        new AdbClient(adbPath), options.Port, () => server.HasActiveSession, new LogUsbObserver(), ToolLocator.BundledApk(), ToolLocator.BundledApkVersionCode());
    tasks.Add(usb.RunAsync(cts.Token));
}

try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
}

return 0;

static void PrintDisplays()
{
    Console.WriteLine($"Sesión de Windows bloqueada: {(SessionLock.IsLocked() ? "sí (no se puede capturar)" : "no")}");
    Console.WriteLine("Pantallas (GDI):");
    foreach (var display in DisplayDevices.List())
    {
        Console.WriteLine($"  {display}{(VirtualDisplay.IsVirtual(display) ? "  <- monitor virtual NexScreen" : "")}");
        if (VirtualDisplay.IsVirtual(display))
        {
            var modes = DisplayDevices.Modes(display.DeviceName).Select(m => $"{m.Width}x{m.Height}@{m.RefreshRate}");
            Console.WriteLine($"      modos: {string.Join(", ", modes)}");
            var scale = DisplayConfig.GetVirtualScale();
            Console.WriteLine($"      escala: {(scale is null ? "(no está en el escritorio)" : $"{scale} %")}");
        }
    }

    Console.WriteLine("Caminos CCD (fuente -> monitor):");
    foreach (var path in DisplayConfig.DescribePaths()) Console.WriteLine($"  {path}");

    Console.WriteLine("Salidas DXGI del adaptador 0 (índice para --monitor):");
    var outputs = DxgiOutputs.ListDefaultAdapterOutputs();
    for (var i = 0; i < outputs.Count; i++) Console.WriteLine($"  {i}: {outputs[i]}");
}

/// <summary>En la consola los avisos de USB ya quedan en el log.</summary>
file sealed class LogUsbObserver : IUsbObserver
{
    public void OnTabletLaunched(string device)
    {
    }

    public void OnTabletUnauthorized(string device)
    {
    }

    public void OnAppMissing(string device)
    {
    }

    public void OnUsbError(string message)
    {
    }
}

/// <summary>En la consola solo hace falta mostrar el PIN de emparejamiento (no va al log).</summary>
file sealed class ConsoleStreamObserver : IStreamObserver
{
    public void OnClientConnected(string device, int width, int height)
    {
    }

    public void OnStats(StreamStats stats)
    {
    }

    public void OnClientDisconnected(string? reason)
    {
    }

    public void OnPairingStarted(string device, string pin, CancellationTokenSource cancel)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($">>> Código para emparejar {device}: {Pairing.Format(pin)}");
        Console.ResetColor();
    }

    public void OnPairingFinished(string device, bool paired)
    {
    }
}
