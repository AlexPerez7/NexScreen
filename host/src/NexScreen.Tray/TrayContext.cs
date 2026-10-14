using System.ComponentModel;
using System.Diagnostics;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Net;
using NexScreen.Core.Update;
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

    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private static readonly Version CurrentVersion =
        typeof(TrayContext).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private static readonly string AppVersion = CurrentVersion.ToString(3);

    private static readonly string UpdatesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexScreen", "updates");

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

    // Actualizaciones: la versión nueva encontrada (si hay) y su opción del menú.
    private readonly UpdateClient _updates = new(AppVersion);
    private readonly ToolStripMenuItem _installUpdate = new() { Visible = false };
    private UpdateRelease? _availableUpdate;
    private bool _updating;
    private Action? _balloonClick;

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
        menu.Items.Add(new ToolStripMenuItem($"Buscar actualizaciones (versión {AppVersion})", null, async (_, _) => await CheckForUpdateAsync(manual: true)));
        _installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        menu.Items.Add(_installUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, async (_, _) => await ExitAsync()));

        _notifyIcon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusBalloon();
        _notifyIcon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
        SetState(TrayState.Waiting, "Iniciando…");

        _ = StartServerAsync();
        if (_settings.CheckForUpdates) _ = CheckForUpdatesPeriodicallyAsync();
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
            ShowBalloon(10_000, "NexScreen no pudo iniciar", error, ToolTipIcon.Error);
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
                    ShowBalloon(10_000, "NexScreen se detuvo", ex.Message, ToolTipIcon.Error);
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
            ShowBalloon(5_000, "NexScreen", "La autoconexión USB no está disponible (no se encontró adb).", ToolTipIcon.Warning);
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

    // ---- Actualizaciones ----

    private async Task CheckForUpdatesPeriodicallyAsync()
    {
        try
        {
            await Task.Delay(FirstUpdateCheckDelay, _appCts.Token);
            while (true)
            {
                await CheckForUpdateAsync(manual: false);
                await Task.Delay(UpdateCheckInterval, _appCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Consulta GitHub. Si hay una versión nueva muestra la opción para instalarla y la avisa con un
    /// globo (una sola vez por versión si la búsqueda es automática).
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updating) return;
        UpdateRelease? release;
        try
        {
            release = await _updates.GetLatestAsync(_appCts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (_appCts.IsCancellationRequested) return;
            Log.Warn($"No se pudo buscar actualizaciones: {ex.Message}");
            if (manual) ShowBalloon(5_000, "NexScreen", "No se pudo consultar si hay una versión nueva. Revisa la conexión a internet.", ToolTipIcon.Warning);
            return;
        }

        if (release is null || !release.IsNewerThan(CurrentVersion))
        {
            if (manual) ShowBalloon(5_000, "NexScreen está al día", $"Tienes la última versión ({AppVersion}).", ToolTipIcon.Info);
            return;
        }

        var version = release.Version.ToString(3);
        if (_availableUpdate?.Version != release.Version) Log.Info($"Hay una versión nueva de NexScreen: {version}");
        _availableUpdate = release;
        _installUpdate.Text = $"Instalar la versión {version}";
        _installUpdate.Visible = true;

        if (manual || _settings.NotifiedUpdate != version)
        {
            _settings.NotifiedUpdate = version;
            _settings.Save();
            ShowBalloon(15_000, $"NexScreen {version} disponible",
                "Haz clic aquí para instalarla (o usa la opción del menú de NexScreen).", ToolTipIcon.Info,
                () => _ = InstallUpdateAsync());
        }
    }

    /// <summary>
    /// Descarga el instalador, detiene el servidor (para quitar el monitor virtual antes de que el
    /// instalador cierre la app) y lo ejecuta en modo silencioso. El instalador vuelve a abrir
    /// NexScreen al terminar (<c>/RELAUNCH=1</c>). Si el usuario cancela el permiso de
    /// administrador, el servidor vuelve a arrancar y no pasa nada.
    /// </summary>
    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } release || _updating || _exiting) return;
        _updating = true;
        var version = release.Version.ToString(3);
        var previousStatus = _status.Text ?? "";
        _installUpdate.Enabled = false;
        _installUpdate.Text = $"Descargando la versión {version}…";

        try
        {
            string installer;
            try
            {
                DeleteOldInstallers(release.InstallerName);
                installer = await _updates.DownloadAsync(release, UpdatesDirectory, _appCts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                           or TaskCanceledException)
            {
                if (_appCts.IsCancellationRequested) return;
                Log.Error($"No se pudo descargar la versión {version}: {ex.Message}");
                ShowBalloon(8_000, "No se pudo descargar la actualización", ex.Message, ToolTipIcon.Error);
                return;
            }

            Log.Info($"Instalando la versión {version} ({installer})");
            SetState(TrayState.Waiting, $"Instalando la versión {version}…");
            await StopServerAsync();
            try
            {
                // UseShellExecute: el instalador pide permisos de administrador (UAC).
                Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
                {
                    UseShellExecute = true,
                });
            }
            catch (Win32Exception ex)
            {
                // 1223: el usuario canceló el pedido de permisos.
                if (ex.NativeErrorCode != 1223)
                {
                    Log.Error($"No se pudo ejecutar el instalador: {ex.Message}");
                    ShowBalloon(8_000, "No se pudo instalar la actualización", ex.Message, ToolTipIcon.Error);
                }
                else
                {
                    Log.Info("Se canceló la instalación de la actualización");
                }

                SetState(TrayState.Waiting, previousStatus);
                await StartServerAsync();
                return;
            }

            await ExitAsync();
        }
        finally
        {
            _updating = false;
            if (!_exiting)
            {
                _installUpdate.Enabled = true;
                _installUpdate.Text = $"Instalar la versión {version}";
            }
        }
    }

    private static void DeleteOldInstallers(string keep)
    {
        if (!Directory.Exists(UpdatesDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(UpdatesDirectory))
        {
            if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- Estado ----

    /// <summary>Muestra un globo. <paramref name="onClick"/> se ejecuta si el usuario hace clic en él.</summary>
    private void ShowBalloon(int timeout, string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _notifyIcon.ShowBalloonTip(timeout, title, text, icon);
    }

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
        ShowBalloon(5_000, "NexScreen", text, ToolTipIcon.Info);
    }

    void IStreamObserver.OnClientConnected(string device, int width, int height) => _ui.Post(_ =>
    {
        _device = device;
        SetState(TrayState.Connected, $"Conectada: {device} ({width}×{height})");
        ShowBalloon(3_000, "Tablet conectada", device, ToolTipIcon.None);
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
            ShowBalloon(5_000, "Tablet desconectada", reason, ToolTipIcon.Warning);
        }
    }, null);

    void IUsbObserver.OnTabletLaunched(string device) => _ui.Post(_ =>
    {
        if (_device is null) SetState(TrayState.Waiting, $"Abriendo NexScreen en {device}…");
    }, null);

    void IUsbObserver.OnTabletUnauthorized(string device) => _ui.Post(_ =>
        ShowBalloon(10_000, "Autoriza la depuración USB",
            $"En la pantalla de {device} acepta \"Permitir depuración USB\" (marca \"Permitir siempre\").",
            ToolTipIcon.Warning), null);

    void IUsbObserver.OnAppMissing(string device) => _ui.Post(_ =>
        ShowBalloon(10_000, "Falta la app en la tablet",
            $"Instala la app NexScreen en {device} para usarla como monitor.", ToolTipIcon.Warning), null);

    void IUsbObserver.OnUsbError(string message) => _ui.Post(_ =>
        ShowBalloon(8_000, "Problema con la conexión USB", message, ToolTipIcon.Warning), null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Dispose();
            _icons.Dispose();
            _serverCts?.Dispose();
            _appCts.Dispose();
            _updates.Dispose();
        }

        base.Dispose(disposing);
    }
}
