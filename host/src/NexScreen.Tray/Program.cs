using System.Reflection;
using NexScreen.Core;
using NexScreen.Core.Protocol;

namespace NexScreen.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Una sola instancia por usuario: el servidor ocupa un puerto fijo.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\NexScreen.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "NexScreen ya está en ejecución. Busca su ícono junto al reloj de Windows.",
                "NexScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexScreen", "logs");
        Directory.CreateDirectory(logDirectory);
        Log.ConsoleEnabled = false;
        Log.FilePath = Path.Combine(logDirectory, "nexscreen.log");

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"NexScreen {version?.ToString(3)} (protocolo v{Wire.Version}) iniciado");

        Application.ThreadException += (_, e) => Log.Error($"Error no controlado: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Error fatal: {e.ExceptionObject}");

        // Configura DPI por monitor (PerMonitorV2, ver el .csproj): las coordenadas de pantalla
        // del monitor virtual y del input quedan en píxeles físicos.
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
