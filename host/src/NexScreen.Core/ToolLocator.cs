using System.ComponentModel;
using System.Diagnostics;

namespace NexScreen.Core;

/// <summary>
/// Encuentra las herramientas externas (ffmpeg, adb): primero en la carpeta tools\ junto al
/// ejecutable (así las deja el instalador) y si no, en el PATH (entorno de desarrollo).
/// </summary>
internal static class ToolLocator
{
    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools");

    public static string Find(string name)
    {
        var bundled = Path.Combine(ToolsDirectory, name + ".exe");
        return File.Exists(bundled) ? bundled : name;
    }

    /// <summary>True si el ejecutable existe y responde al argumento indicado.</summary>
    public static bool Runs(string path, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments)
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
}
