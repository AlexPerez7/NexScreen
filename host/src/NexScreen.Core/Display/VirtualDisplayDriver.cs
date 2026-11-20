using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NexScreen.Core.Display;

/// <summary>Estado del driver del monitor virtual.</summary>
internal enum DriverState
{
    /// <summary>No hay un dispositivo del driver en el sistema.</summary>
    NotInstalled,

    /// <summary>Instalado y publicando su adaptador de pantalla.</summary>
    Working,

    /// <summary>
    /// Instalado pero sin adaptador: el driver no arrancó o se cayó (por ejemplo, tras actualizar
    /// Windows). Reiniciar el dispositivo suele arreglarlo.
    /// </summary>
    Broken,
}

internal enum RepairResult
{
    Repaired,
    StillBroken,

    /// <summary>El usuario canceló el aviso de permisos (UAC).</summary>
    Cancelled,
    NotInstalled,
}

/// <summary>
/// Salud del Virtual Display Driver. Cuando falla, <see cref="VirtualDisplay.Find"/> deja de ver su
/// adaptador aunque el dispositivo siga instalado; eso es lo que distingue "roto" de "no instalado".
/// </summary>
internal static partial class VirtualDisplayDriver
{
    private const string HardwareId = "MttVDD";
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int ErrorCancelled = 1223;

    public static DriverState Check() =>
        Evaluate(FindInstanceId() is not null, VirtualDisplay.Find() is not null);

    internal static DriverState Evaluate(bool deviceInstalled, bool adapterPresent) =>
        adapterPresent ? DriverState.Working
        : deviceInstalled ? DriverState.Broken
        : DriverState.NotInstalled;

    /// <summary>True si alguno de los hardware IDs del dispositivo es el del driver.</summary>
    internal static bool IsDriverDevice(IEnumerable<string> hardwareIds) =>
        hardwareIds.Any(id => id.Contains(HardwareId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Instance ID (por ejemplo ROOT\DISPLAY\0000) del dispositivo del driver, o null si no está instalado.</summary>
    public static string? FindInstanceId()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT");
            if (root is null) return null;
            foreach (var group in root.GetSubKeyNames())
            {
                using var groupKey = root.OpenSubKey(group);
                if (groupKey is null) continue;
                foreach (var instance in groupKey.GetSubKeyNames())
                {
                    using var key = groupKey.OpenSubKey(instance);
                    if (key?.GetValue("HardwareID") is string[] ids && IsDriverDevice(ids))
                    {
                        return $@"ROOT\{group}\{instance}";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"No se pudo leer el registro de dispositivos: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Reinicia (deshabilita y vuelve a habilitar) el dispositivo del driver. Requiere administrador:
    /// pide permisos con el aviso de Windows (UAC) y espera a que el adaptador reaparezca.
    /// </summary>
    public static async Task<RepairResult> RepairAsync(CancellationToken ct)
    {
        var instanceId = FindInstanceId();
        if (instanceId is null) return RepairResult.NotInstalled;
        if (!InstanceIdPattern().IsMatch(instanceId)) return RepairResult.NotInstalled;

        var script =
            $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false";
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Log.Info($"Reiniciando el dispositivo del monitor virtual ({instanceId})");
        try
        {
            using var process = Process.Start(start);
            if (process is not null) await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Warn("Se canceló el permiso de administrador para reparar el monitor virtual");
            return RepairResult.Cancelled;
        }

        var deadline = DateTime.UtcNow + RepairTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (VirtualDisplay.Find() is not null)
            {
                Log.Info("Monitor virtual reparado");
                return RepairResult.Repaired;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        Log.Warn("El driver del monitor virtual sigue sin responder después de reiniciarlo");
        return RepairResult.StillBroken;
    }

    [GeneratedRegex(@"^ROOT\\[A-Za-z0-9_]+\\[0-9]{4}$")]
    private static partial Regex InstanceIdPattern();
}
