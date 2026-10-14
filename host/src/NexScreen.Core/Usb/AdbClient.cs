using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NexScreen.Core.Usb;

internal enum AdbDeviceState
{
    Device,
    Unauthorized,
    Offline,
    Other,
}

internal sealed record AdbDevice(string Serial, AdbDeviceState State, string Model)
{
    /// <summary>adb reemplaza los espacios del modelo por "_" (SM_X510).</summary>
    public string DisplayName => string.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ');
}

internal sealed record AdbResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Ejecuta comandos de adb (Android Debug Bridge) con timeout.</summary>
internal sealed class AdbClient : IAdb
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public AdbClient(string adbPath) => AdbPath = adbPath;

    public string AdbPath { get; }

    public async Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken ct)
    {
        var result = await RunAsync(["devices", "-l"], ct).ConfigureAwait(false);
        return result.Success ? ParseDevices(result.Output) : [];
    }

    public Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct) =>
        RunAsync(["-s", serial, "shell", command], ct);

    public Task<AdbResult> ReverseAsync(string serial, int port, CancellationToken ct) =>
        RunAsync(["-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}"], ct);

    public Task<AdbResult> InstallAsync(string serial, string apkPath, CancellationToken ct) =>
        RunAsync(["-s", serial, "install", "-r", apkPath], ct, TimeSpan.FromMinutes(2));

    /// <summary>Salida de "adb devices -l" → dispositivos. Ignora el encabezado y los mensajes del daemon.</summary>
    internal static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith("List of devices", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var state = parts[1] switch
            {
                "device" => AdbDeviceState.Device,
                "unauthorized" => AdbDeviceState.Unauthorized,
                "offline" => AdbDeviceState.Offline,
                _ => AdbDeviceState.Other,
            };
            var model = parts.Skip(2).FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..] ?? "";
            devices.Add(new AdbDevice(parts[0], state, model));
        }

        return devices;
    }

    private async Task<AdbResult> RunAsync(string[] arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("No se pudo iniciar adb");
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new AdbResult(process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AdbResult(-1, $"adb {string.Join(' ', arguments)}: timeout");
        }
        catch (Win32Exception ex)
        {
            return new AdbResult(-1, $"No se pudo ejecutar adb ({AdbPath}): {ex.Message}");
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
        }
    }
}
