using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using NexScreen.Host;
using NexScreen.Host.Net;
using System.Text;
using NexScreen.Host.Protocol;

Console.OutputEncoding = Encoding.UTF8;

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

Log.Verbose = options.Verbose;
var version = Assembly.GetExecutingAssembly().GetName().Version;
Log.Info($"NexScreen host {version?.ToString(3)} (protocolo v{Wire.Version})");

if (!FfmpegAvailable(options.FfmpegPath))
{
    Log.Error($"No se encontró ffmpeg ('{options.FfmpegPath}'). Instalalo con: winget install Gyan.FFmpeg");
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
