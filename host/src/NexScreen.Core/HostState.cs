using System.Text.Json;

namespace NexScreen.Core;

/// <summary>
/// Estado que el host recuerda entre ejecuciones, en %APPDATA%\NexScreen\host-state.json.
/// </summary>
internal sealed class HostState
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexScreen", "host-state.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Última posición del monitor virtual en el escritorio (la que eligió el usuario).</summary>
    public int? VirtualDisplayX { get; set; }

    public int? VirtualDisplayY { get; set; }

    public static HostState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HostState>(File.ReadAllText(path), Json) ?? new HostState()
                : new HostState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo leer {path}: {ex.Message}");
            return new HostState();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo guardar {path}: {ex.Message}");
        }
    }
}
