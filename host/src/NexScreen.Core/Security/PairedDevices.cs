using System.Text.Json;

namespace NexScreen.Core.Security;

internal sealed record PairedDevice(string Name, string Fingerprint, DateTimeOffset PairedAt);

/// <summary>
/// Tablets emparejadas por WiFi (huella de su certificado TLS), en
/// %APPDATA%\NexScreen\paired-devices.json. Es seguro usarla desde varios hilos.
/// </summary>
internal sealed class PairedDevices
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexScreen", "paired-devices.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private List<PairedDevice> _devices;

    public PairedDevices(string? path = null)
    {
        _path = path ?? DefaultPath;
        _devices = Load(_path);
    }

    public IReadOnlyList<PairedDevice> All
    {
        get
        {
            lock (_lock) return _devices.ToList();
        }
    }

    public bool Contains(string fingerprint)
    {
        lock (_lock) return _devices.Any(d => d.Fingerprint == fingerprint);
    }

    public void Add(string name, string fingerprint)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.Fingerprint == fingerprint);
            _devices.Add(new PairedDevice(name, fingerprint, DateTimeOffset.Now));
            Save();
        }
    }

    public void Remove(string fingerprint)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.Fingerprint == fingerprint) > 0) Save();
        }
    }

    private static List<PairedDevice> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Sin la lista las tablets vuelven a pedir el PIN: molesto pero seguro.
            Log.Warn($"No se pudo leer {path}: {ex.Message}");
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_devices, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo guardar {_path}: {ex.Message}");
        }
    }
}
