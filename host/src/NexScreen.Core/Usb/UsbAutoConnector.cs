namespace NexScreen.Core.Usb;

/// <summary>Avisos de la autoconexión USB para la interfaz. Se llaman desde un hilo de fondo.</summary>
internal interface IUsbObserver
{
    /// <summary>Se armó el túnel y se abrió la app en la tablet.</summary>
    void OnTabletLaunched(string device);

    /// <summary>La tablet está enchufada pero falta aceptar la depuración USB en su pantalla.</summary>
    void OnTabletUnauthorized(string device);

    /// <summary>La app NexScreen no está instalada en la tablet y no hay APK para instalarla.</summary>
    void OnAppMissing(string device);

    void OnUsbError(string message);
}

/// <summary>Comandos de adb que usa la autoconexión (interfaz para poder testearla sin adb).</summary>
internal interface IAdb
{
    Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken ct);

    Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct);

    Task<AdbResult> ReverseAsync(string serial, int port, CancellationToken ct);

    Task<AdbResult> InstallAsync(string serial, string apkPath, CancellationToken ct);
}

/// <summary>
/// Detecta tablets enchufadas por USB y las deja listas sin intervención: túnel `adb reverse`
/// hacia el puerto del host, instalación de la app si hace falta (con el APK que deja el
/// instalador) y apertura de la app conectándose sola. Cada tablet se prepara una vez por
/// enchufada; si el usuario cierra la app no se vuelve a abrir hasta que la desenchufe.
/// </summary>
internal sealed class UsbAutoConnector
{
    public const string AppPackage = "dev.nexscreen.client";
    public const string AutoConnectExtra = "dev.nexscreen.client.AUTO_CONNECT";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IAdb _adb;
    private readonly int _port;
    private readonly Func<bool> _sessionActive;
    private readonly IUsbObserver _observer;
    private readonly string? _apkPath;
    private readonly HashSet<string> _prepared = new();
    private readonly HashSet<string> _warnedUnauthorized = new();

    public UsbAutoConnector(IAdb adb, int port, Func<bool> sessionActive, IUsbObserver observer, string? apkPath = null)
    {
        _adb = adb;
        _port = port;
        _sessionActive = sessionActive;
        _observer = observer;
        _apkPath = apkPath;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info("Autoconexión USB activa");
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Una vuelta de detección. Pública para los tests.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var devices = await _adb.DevicesAsync(ct).ConfigureAwait(false);
        var present = devices.Select(d => d.Serial).ToHashSet();
        _prepared.RemoveWhere(serial => !present.Contains(serial));
        _warnedUnauthorized.RemoveWhere(serial => !present.Contains(serial));

        foreach (var device in devices)
        {
            if (device.State == AdbDeviceState.Unauthorized && _warnedUnauthorized.Add(device.Serial))
            {
                Log.Warn($"{device.DisplayName}: falta aceptar la depuración USB en la tablet");
                _observer.OnTabletUnauthorized(device.DisplayName);
            }

            if (device.State != AdbDeviceState.Device || !_prepared.Add(device.Serial)) continue;
            Log.Info($"Tablet USB detectada: {device.DisplayName} ({device.Serial})");
            await PrepareAsync(device, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Vuelve a preparar y abrir la app en todas las tablets enchufadas (opción del menú).</summary>
    public async Task ConnectNowAsync(CancellationToken ct)
    {
        foreach (var device in await _adb.DevicesAsync(ct).ConfigureAwait(false))
        {
            if (device.State == AdbDeviceState.Device) await PrepareAsync(device, ct).ConfigureAwait(false);
        }
    }

    private async Task PrepareAsync(AdbDevice device, CancellationToken ct)
    {
        var reverse = await _adb.ReverseAsync(device.Serial, _port, ct).ConfigureAwait(false);
        if (!reverse.Success)
        {
            Fail(device, $"no se pudo abrir el túnel USB: {reverse.Output.Trim()}");
            return;
        }

        if (_sessionActive())
        {
            Log.Info($"{device.DisplayName}: túnel listo (ya hay una sesión activa, no se abre la app)");
            return;
        }

        var package = await _adb.ShellAsync(device.Serial, $"pm path {AppPackage}", ct).ConfigureAwait(false);
        if (!package.Output.Contains("package:", StringComparison.Ordinal))
        {
            if (_apkPath is null || !File.Exists(_apkPath))
            {
                Log.Warn($"{device.DisplayName}: la app NexScreen no está instalada");
                _observer.OnAppMissing(device.DisplayName);
                return;
            }

            Log.Info($"{device.DisplayName}: instalando la app NexScreen");
            var install = await _adb.InstallAsync(device.Serial, _apkPath, ct).ConfigureAwait(false);
            if (!install.Success)
            {
                Fail(device, $"no se pudo instalar la app: {install.Output.Trim()}");
                return;
            }
        }

        var start = await _adb.ShellAsync(
            device.Serial, $"am start -n {AppPackage}/.MainActivity --es {AutoConnectExtra} usb", ct).ConfigureAwait(false);
        if (!start.Success || start.Output.Contains("Error", StringComparison.Ordinal))
        {
            Fail(device, $"no se pudo abrir la app: {start.Output.Trim()}");
            return;
        }

        Log.Info($"{device.DisplayName}: app abierta, conectando por USB");
        _observer.OnTabletLaunched(device.DisplayName);
    }

    private void Fail(AdbDevice device, string message)
    {
        Log.Warn($"{device.DisplayName}: {message}");
        _observer.OnUsbError($"{device.DisplayName}: {message}");
    }
}
