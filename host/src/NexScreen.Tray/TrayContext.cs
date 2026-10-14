using System.Diagnostics;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Net;
using NexScreen.Core.Usb;

namespace NexScreen.Tray;

/// <summary>
/// Ícono de NexScreen en la bandeja del sistema: mantiene el servidor corriendo, muestra el estado
/// y permite cambiar las preferencias. Los eventos del servidor llegan desde hilos de fondo y se
/// pasan al hilo de la interfaz.
/// </summary>
internal sealed class TrayContext : ApplicationContext, IStreamObserver, IUsbObserver
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private static readonly string AppVersion =
        typeof(TrayContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly SynchronizationContext _ui = new WindowsFormsSynchronizationContext();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _startWithWindows;

    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private volatile StreamServer? _server;

    // La autoconexión USB vive mientras viva la bandeja (no se reinicia con el servidor): así no
    // vuelve a abrir la app en una tablet que ya estaba enchufada y se está reconectando sola.
    private readonly CancellationTokenSource _appCts = new();
    private UsbAutoConnector? _usb;
    private Task _usbTask = Task.CompletedTask;
    private string? _device;
    private bool _exiting;

    public TrayContext()
    {
        if (!_settings.StartupConfigured)
        {
            StartupRegistration.Set(true);
            _settings.StartupConfigured = true;
            _settings.Save();
            Log.Info("NexScreen se configuró para iniciar con Windows");
        }
        else if (StartupRegistration.PointsElsewhere)
        {
            // El usuario quiere iniciar con Windows: que sea con este NexScreen.exe.
            StartupRegistration.Set(true);
            Log.Info($"Inicio con Windows actualizado a {Environment.ProcessPath}");
        }

        _startWithWindows = new ToolStripMenuItem("Iniciar con Windows", null, (_, _) => ToggleStartWithWindows())
        {
            Checked = StartupRegistration.IsEnabled,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(ModeMenu());
        menu.Items.Add(PositionMenu());
        menu.Items.Add(QualityMenu());
        menu.Items.Add(ScaleMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Abrir NexScreen en la tablet (USB)", null, async (_, _) => await ConnectUsbNowAsync()));
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new ToolStripMenuItem("Abrir el registro", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, async (_, _) => await ExitAsync()));

        _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusBalloon();
        SetState(TrayState.Waiting, "Iniciando…");

        _ = StartServerAsync();
    }

    // ---- Menú ----

    private ToolStripMenuItem ModeMenu() => RadioMenu("Modo",
        [
            ("Extender el escritorio (monitor nuevo)", ScreenMode.Extend),
            ("Espejar la pantalla principal", ScreenMode.Mirror),
        ],
        () => _settings.Mode == ScreenMode.Mirror ? ScreenMode.Mirror : ScreenMode.Extend,
        mode => _settings.Mode = mode);

    private ToolStripMenuItem PositionMenu() => RadioMenu<DisplayPosition?>("Posición de la tablet",
        [
            ("Donde la dejé la última vez", null),
            ("A la izquierda", DisplayPosition.Left),
            ("A la derecha", DisplayPosition.Right),
            ("Arriba", DisplayPosition.Above),
            ("Abajo", DisplayPosition.Below),
        ],
        () => _settings.Position,
        position => _settings.Position = position);

    private ToolStripMenuItem QualityMenu() => RadioMenu("Calidad",
        [
            ($"Alta ({AppSettings.BitrateMbps(Quality.High)} Mbps)", Quality.High),
            ($"Normal ({AppSettings.BitrateMbps(Quality.Normal)} Mbps)", Quality.Normal),
            ($"Ahorro ({AppSettings.BitrateMbps(Quality.Saver)} Mbps, para WiFi lento)", Quality.Saver),
        ],
        () => _settings.Quality,
        quality => _settings.Quality = quality);

    private ToolStripMenuItem ScaleMenu() => RadioMenu<int?>("Escala de la tablet",
        [
            ("100 % (texto más chico, más espacio)", 100),
            ("125 %", 125),
            ("150 % (recomendada)", 150),
            ("175 %", 175),
            ("200 % (texto más grande)", 200),
            ("No cambiarla (la elijo en Configuración de Windows)", null),
        ],
        () => _settings.TabletScale,
        scale => _settings.TabletScale = scale);

    /// <summary>Submenú de opciones excluyentes. Cambiar la opción guarda y reinicia el servidor.</summary>
    private ToolStripMenuItem RadioMenu<T>(string title, (string Text, T Value)[] options, Func<T> current, Action<T> apply)
    {
        var menu = new ToolStripMenuItem(title);
        foreach (var (text, value) in options)
        {
            var item = new ToolStripMenuItem(text) { Tag = value };
            item.Click += async (_, _) =>
            {
                if (EqualityComparer<T>.Default.Equals(current(), value)) return;
                apply(value);
                _settings.Save();
                await RestartServerAsync();
            };
            menu.DropDownItems.Add(item);
        }

        menu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in menu.DropDownItems)
            {
                item.Checked = EqualityComparer<T>.Default.Equals((T)item.Tag!, current());
            }
        };
        return menu;
    }

    private void ToggleStartWithWindows()
    {
        StartupRegistration.Set(!StartupRegistration.IsEnabled);
        _startWithWindows.Checked = StartupRegistration.IsEnabled;
    }

    private static void OpenLog()
    {
        if (Log.FilePath is { } path && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    // ---- Servidor ----

    private async Task StartServerAsync()
    {
        var options = _settings.ToHostOptions();
        var error = await Task.Run(() => HostStartup.Prepare(options));
        if (error is not null)
        {
            Log.Error(error);
            SetState(TrayState.Error, "No se pudo iniciar");
            _notifyIcon.ShowBalloonTip(10_000, "NexScreen no pudo iniciar", error, ToolTipIcon.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        _serverCts = cts;
        SetState(TrayState.Waiting, "Esperando la tablet");

        var server = new StreamServer(options, this);
        var discovery = new DiscoveryResponder(options.Port, AppVersion);
        _server = server;
        _serverTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(server.RunAsync(cts.Token), discovery.RunAsync(cts.Token));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                Log.Error($"El servidor se detuvo: {ex}");
                _ui.Post(_ =>
                {
                    SetState(TrayState.Error, "El servidor se detuvo");
                    _notifyIcon.ShowBalloonTip(10_000, "NexScreen se detuvo", ex.Message, ToolTipIcon.Error);
                }, null);
            }
        });

        if (_usb is null) StartUsbAutoConnect(options.Port);
    }

    private void StartUsbAutoConnect(int port)
    {
        var adbPath = ToolLocator.Find("adb");
        if (!ToolLocator.Runs(adbPath, "version"))
        {
            Log.Warn("No se encontró adb: la autoconexión USB está desactivada");
            return;
        }

        var usb = new UsbAutoConnector(
            new AdbClient(adbPath), port, () => _server?.HasActiveSession ?? false, this, ToolLocator.BundledApk(), ToolLocator.BundledApkVersionCode());
        _usb = usb;
        _usbTask = Task.Run(async () =>
        {
            try
            {
                await usb.RunAsync(_appCts.Token);
            }
            catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"La autoconexión USB se detuvo: {ex}");
            }
        });
    }

    private async Task ConnectUsbNowAsync()
    {
        if (_usb is null)
        {
            _notifyIcon.ShowBalloonTip(5_000, "NexScreen", "La autoconexión USB no está disponible (no se encontró adb).", ToolTipIcon.Warning);
            return;
        }

        await Task.Run(() => _usb.ConnectNowAsync(CancellationToken.None));
    }

    private async Task StopServerAsync()
    {
        if (_serverCts is null || _serverTask is null) return;
        await _serverCts.CancelAsync();
        // Esperar a que la sesión quite el monitor virtual antes de seguir.
        await Task.WhenAny(_serverTask, Task.Delay(StopTimeout));
        _serverCts.Dispose();
        (_serverCts, _serverTask, _server) = (null, null, null);
    }

    private async Task RestartServerAsync()
    {
        SetState(TrayState.Waiting, "Aplicando cambios…");
        await StopServerAsync();
        await StartServerAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _notifyIcon.Visible = false;
        await _appCts.CancelAsync();
        await StopServerAsync();
        await Task.WhenAny(_usbTask, Task.Delay(StopTimeout));
        _notifyIcon.Dispose();
        _icons.Dispose();
        ExitThread();
    }

    // ---- Estado ----

    private void SetState(TrayState state, string text)
    {
        _status.Text = text;
        _notifyIcon.Icon = _icons[state];
        // El tooltip de NotifyIcon admite como máximo 127 caracteres.
        var tooltip = $"NexScreen: {text}";
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void ShowStatusBalloon()
    {
        var text = _device is null
            ? "Conecta la tablet por USB o WiFi y abre la app NexScreen."
            : _status.Text ?? "";
        _notifyIcon.ShowBalloonTip(5_000, "NexScreen", text, ToolTipIcon.Info);
    }

    void IStreamObserver.OnClientConnected(string device, int width, int height) => _ui.Post(_ =>
    {
        _device = device;
        SetState(TrayState.Connected, $"Conectada: {device} ({width}×{height})");
        _notifyIcon.ShowBalloonTip(3_000, "Tablet conectada", device, ToolTipIcon.None);
    }, null);

    void IStreamObserver.OnStats(double fps, double mbps) => _ui.Post(_ =>
    {
        if (_device is not null) SetState(TrayState.Connected, $"Conectada: {_device} · {fps:F0} fps · {mbps:F1} Mbps");
    }, null);

    void IStreamObserver.OnClientDisconnected(string? reason) => _ui.Post(_ =>
    {
        if (_exiting) return;
        var device = _device;
        _device = null;
        SetState(TrayState.Waiting, "Esperando la tablet");
        if (reason is not null && device is not null)
        {
            _notifyIcon.ShowBalloonTip(5_000, "Tablet desconectada", reason, ToolTipIcon.Warning);
        }
    }, null);

    void IUsbObserver.OnTabletLaunched(string device) => _ui.Post(_ =>
    {
        if (_device is null) SetState(TrayState.Waiting, $"Abriendo NexScreen en {device}…");
    }, null);

    void IUsbObserver.OnTabletUnauthorized(string device) => _ui.Post(_ =>
        _notifyIcon.ShowBalloonTip(10_000, "Autoriza la depuración USB",
            $"En la pantalla de {device} acepta \"Permitir depuración USB\" (marca \"Permitir siempre\").",
            ToolTipIcon.Warning), null);

    void IUsbObserver.OnAppMissing(string device) => _ui.Post(_ =>
        _notifyIcon.ShowBalloonTip(10_000, "Falta la app en la tablet",
            $"Instala la app NexScreen en {device} para usarla como monitor.", ToolTipIcon.Warning), null);

    void IUsbObserver.OnUsbError(string message) => _ui.Post(_ =>
        _notifyIcon.ShowBalloonTip(8_000, "Problema con la conexión USB", message, ToolTipIcon.Warning), null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Dispose();
            _icons.Dispose();
            _serverCts?.Dispose();
            _appCts.Dispose();
        }

        base.Dispose(disposing);
    }
}
