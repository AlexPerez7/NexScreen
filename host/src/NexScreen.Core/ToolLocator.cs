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
        if (File.Exists(bundled)) return bundled;

        // adb suele no estar en el PATH: probar donde lo deja el Android SDK.
        if (name == "adb")
        {
            var sdk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe");
            if (File.Exists(sdk)) return sdk;
        }

        return name;
    }

    /// <summary>APK de la app de la tablet que deja el instalador, si existe.</summary>
    public static string? BundledApk()
    {
        var apk = Path.Combine(ToolsDirectory, "NexScreen.apk");
        return File.Exists(apk) ? apk : null;
    }

    /// <summary>
    /// versionCode del APK incluido (lo escribe installer\build.ps1 en NexScreen.apk.json), o null.
    /// </summary>
    public static int? BundledApkVersionCode()
    {
        var path = Path.Combine(ToolsDirectory, "NexScreen.apk.json");
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("versionCode").GetInt32();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
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
