using System.Text.Json;
using System.Text.Json.Serialization;
using NexScreen.Core;
using NexScreen.Core.Display;
using NexScreen.Core.Protocol;

namespace NexScreen.Tray;

internal enum Quality
{
    High,
    Normal,
    Saver,
}

/// <summary>Preferencias del usuario, en %APPDATA%\NexScreen\settings.json.</summary>
internal sealed class AppSettings
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexScreen", "settings.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ScreenMode Mode { get; set; } = ScreenMode.Auto;

    /// <summary>Null: recordar la última posición en la que el usuario dejó la tablet.</summary>
    public DisplayPosition? Position { get; set; }

    public Quality Quality { get; set; } = Quality.Normal;

    /// <summary>Valor de <see cref="TabletScale"/> para elegir la escala según la pantalla del dispositivo.</summary>
    public const int AutoScale = 0;

    /// <summary>
    /// Escala de Windows para el dispositivo: <see cref="AutoScale"/> la calcula con la densidad de
    /// su pantalla (150 % en una tablet de 11", más en un celular). Null: no tocarla (la elige el
    /// usuario en Configuración). El nombre quedó de cuando solo había tablets; se mantiene por el JSON.
    /// </summary>
    public int? TabletScale { get; set; } = AutoScale;

    public int Port { get; set; } = Wire.DefaultPort;

    /// <summary>
    /// La primera vez que se abre, NexScreen se activa para iniciar con Windows. Lo hace la app y no
    /// el instalador porque el instalador corre como administrador (puede ser otra cuenta) y no
    /// puede escribir en el registro del usuario. Después se respeta lo que elija el usuario.
    /// </summary>
    public bool StartupConfigured { get; set; }

    /// <summary>Buscar versiones nuevas en GitHub al iniciar y cada 24 h.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Última versión nueva que se avisó con un globo (para no repetir el aviso cada día).</summary>
    public string? NotifiedUpdate { get; set; }

    /// <summary>Bitrate máximo para 2304×1440 a 60 fps; el host lo ajusta a la pantalla del dispositivo.</summary>
    public static int BitrateMbps(Quality quality) => quality switch
    {
        Quality.High => 20,
        Quality.Saver => 6,
        _ => 12,
    };

    public HostOptions ToHostOptions() => new()
    {
        Mode = Mode,
        Position = Position,
        BitrateMbps = BitrateMbps(Quality),
        ScalePercent = TabletScale == AutoScale ? null : TabletScale,
        AutoScale = TabletScale == AutoScale,
        Port = Port,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo leer {path}, se usan los valores por defecto: {ex.Message}");
            return new AppSettings();
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
