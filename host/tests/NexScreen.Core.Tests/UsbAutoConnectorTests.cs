using NexScreen.Core.Usb;

namespace NexScreen.Core.Tests;

public class AdbParsingTests
{
    [Fact]
    public void ParsesDevicesListWithModels()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R52XB00LN0W            device product:gts9fewifixx model:SM_X510 device:gts9fewifi transport_id:1
            emulator-5554          unauthorized transport_id:2
            0123456789ABCDEF       offline

            """;

        var devices = AdbClient.ParseDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new AdbDevice("R52XB00LN0W", AdbDeviceState.Device, "SM_X510"), devices[0]);
        Assert.Equal("SM X510", devices[0].DisplayName);
        Assert.Equal(AdbDeviceState.Unauthorized, devices[1].State);
        Assert.Equal("emulator-5554", devices[1].DisplayName);
        Assert.Equal(AdbDeviceState.Offline, devices[2].State);
    }

    [Fact]
    public void EmptyListHasNoDevices()
    {
        Assert.Empty(AdbClient.ParseDevices("List of devices attached\r\n\r\n"));
    }
}

public class UsbAutoConnectorTests
{
    private const string Serial = "R52XB00LN0W";

    private sealed class FakeAdb : IAdb
    {
        public List<AdbDevice> Devices { get; } = [new(Serial, AdbDeviceState.Device, "SM_X510")];

        /// <summary>versionCode de la app en la tablet; null = no instalada.</summary>
        public int? InstalledVersion { get; set; } = 400;

        /// <summary>La app instalada tiene otra firma: install -r falla hasta desinstalarla.</summary>
        public bool SignatureMismatch { get; set; }

        public List<string> Commands { get; } = [];

        public bool AppInstalled
        {
            set => InstalledVersion = value ? 400 : null;
        }

        public Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AdbDevice>>(Devices.ToList());

        public Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct)
        {
            Commands.Add($"shell {command}");
            var output = command.StartsWith("dumpsys package", StringComparison.Ordinal)
                ? InstalledVersion is { } version
                    ? $"Packages:\n  Package [{UsbAutoConnector.AppPackage}] (1a2b3c):\n    versionCode={version} minSdk=26 targetSdk=35"
                    : "Unable to find package"
                : "Starting: Intent";
            return Task.FromResult(new AdbResult(0, output));
        }

        public Task<AdbResult> ReverseAsync(string serial, int port, CancellationToken ct)
        {
            Commands.Add($"reverse {port}");
            return Task.FromResult(new AdbResult(0, $"{port}"));
        }

        public Task<AdbResult> InstallAsync(string serial, string apkPath, CancellationToken ct)
        {
            Commands.Add("install");
            if (SignatureMismatch && InstalledVersion is not null)
            {
                return Task.FromResult(new AdbResult(1, "Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: signatures do not match]"));
            }

            InstalledVersion = 500;
            return Task.FromResult(new AdbResult(0, "Success"));
        }

        public Task<AdbResult> UninstallAsync(string serial, string package, CancellationToken ct)
        {
            Commands.Add("uninstall");
            InstalledVersion = null;
            return Task.FromResult(new AdbResult(0, "Success"));
        }
    }

    private sealed class RecordingObserver : IUsbObserver
    {
        public List<string> Events { get; } = [];

        public void OnTabletLaunched(string device) => Events.Add($"launched {device}");

        public void OnTabletUnauthorized(string device) => Events.Add($"unauthorized {device}");

        public void OnAppMissing(string device) => Events.Add($"missing {device}");

        public void OnUsbError(string message) => Events.Add($"error {message}");
    }

    [Fact]
    public async Task PreparesAndLaunchesAppOncePerPlug()
    {
        var adb = new FakeAdb();
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => false, observer);

        await connector.PollOnceAsync(CancellationToken.None);
        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["launched SM X510"], observer.Events);
        Assert.Contains("reverse 27190", adb.Commands);
        Assert.Contains(adb.Commands, c => c.Contains("am start") && c.Contains(UsbAutoConnector.AutoConnectExtra));
    }

    [Fact]
    public async Task ReplugLaunchesAgain()
    {
        var adb = new FakeAdb();
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => false, observer);

        await connector.PollOnceAsync(CancellationToken.None);
        var device = adb.Devices[0];
        adb.Devices.Clear();
        await connector.PollOnceAsync(CancellationToken.None);
        adb.Devices.Add(device);
        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, observer.Events.Count(e => e.StartsWith("launched", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OnlyOpensTunnelWhenSessionIsActive()
    {
        var adb = new FakeAdb();
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => true, observer);

        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["reverse 27190"], adb.Commands);
        Assert.Empty(observer.Events);
    }

    [Fact]
    public async Task ReportsMissingAppWithoutApk()
    {
        var adb = new FakeAdb { AppInstalled = false };
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => false, observer, apkPath: null);

        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["missing SM X510"], observer.Events);
        Assert.DoesNotContain(adb.Commands, c => c.Contains("am start"));
    }

    [Fact]
    public async Task InstallsBundledApkWhenAppIsMissing()
    {
        var apk = Path.GetTempFileName();
        try
        {
            var adb = new FakeAdb { AppInstalled = false };
            var observer = new RecordingObserver();
            var connector = new UsbAutoConnector(adb, 27190, () => false, observer, apk);

            await connector.PollOnceAsync(CancellationToken.None);

            Assert.Contains("install", adb.Commands);
            Assert.Equal(["launched SM X510"], observer.Events);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task UpdatesOutdatedApp()
    {
        await WithApk(async apk =>
        {
            var adb = new FakeAdb { InstalledVersion = 400 };
            var connector = new UsbAutoConnector(adb, 27190, () => false, new RecordingObserver(), apk, apkVersionCode: 500);

            await connector.PollOnceAsync(CancellationToken.None);

            Assert.Contains("install", adb.Commands);
            Assert.Equal(500, adb.InstalledVersion);
        });
    }

    [Fact]
    public async Task KeepsAppThatIsUpToDate()
    {
        await WithApk(async apk =>
        {
            var adb = new FakeAdb { InstalledVersion = 500 };
            var connector = new UsbAutoConnector(adb, 27190, () => false, new RecordingObserver(), apk, apkVersionCode: 500);

            await connector.PollOnceAsync(CancellationToken.None);

            Assert.DoesNotContain("install", adb.Commands);
        });
    }

    [Fact]
    public async Task ReinstallsAppSignedWithAnotherKey()
    {
        await WithApk(async apk =>
        {
            var adb = new FakeAdb { InstalledVersion = 400, SignatureMismatch = true };
            var observer = new RecordingObserver();
            var connector = new UsbAutoConnector(adb, 27190, () => false, observer, apk, apkVersionCode: 500);

            await connector.PollOnceAsync(CancellationToken.None);

            Assert.Equal(["install", "uninstall", "install"], adb.Commands.Where(c => !c.StartsWith("shell") && !c.StartsWith("reverse")));
            Assert.Equal(["launched SM X510"], observer.Events);
        });
    }

    [Fact]
    public async Task UsesOutdatedAppWhenThereIsNoApk()
    {
        var adb = new FakeAdb { InstalledVersion = 100 };
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => false, observer, apkPath: null, apkVersionCode: 500);

        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["launched SM X510"], observer.Events);
    }

    private static async Task WithApk(Func<string, Task> test)
    {
        var apk = Path.GetTempFileName();
        try
        {
            await test(apk);
        }
        finally
        {
            File.Delete(apk);
        }
    }

    [Fact]
    public async Task WarnsOnceAboutUnauthorizedTablet()
    {
        var adb = new FakeAdb();
        adb.Devices[0] = adb.Devices[0] with { State = AdbDeviceState.Unauthorized };
        var observer = new RecordingObserver();
        var connector = new UsbAutoConnector(adb, 27190, () => false, observer);

        await connector.PollOnceAsync(CancellationToken.None);
        await connector.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["unauthorized SM X510"], observer.Events);
        Assert.Empty(adb.Commands);
    }
}
