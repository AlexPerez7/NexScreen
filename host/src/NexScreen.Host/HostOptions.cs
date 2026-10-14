using System.Globalization;
using NexScreen.Host.Display;
using NexScreen.Host.Protocol;

namespace NexScreen.Host;

internal enum EncoderKind
{
    Auto,
    Qsv,
    X264,
}

internal enum ScreenMode
{
    /// <summary>Extiende con el monitor virtual si el driver está instalado; si no, espeja.</summary>
    Auto,
    Extend,
    Mirror,
}

internal sealed class HostOptions
{
    public const string Usage = """
        Uso: nexscreen-host [opciones]

          --port <n>        Puerto TCP (por defecto 27190)
          --mode <m>        auto | extend | mirror (por defecto auto: extiende si está el driver virtual)
          --position <p>    right | left | above | below: dónde ubicar la tablet al extender
                            (por defecto: donde la dejaste la última vez en Configuración; right la primera vez)
          --monitor <n>     Al espejar: índice del monitor a capturar, orden DXGI (por defecto 0)
          --fps <n>         Cuadros por segundo (por defecto 60)
          --bitrate <n>     Bitrate en Mbps (por defecto 12)
          --encoder <e>     auto | qsv | x264 (por defecto auto: Quick Sync con fallback a x264)
          --ffmpeg <ruta>   Ruta a ffmpeg.exe (por defecto se busca en PATH)
          --list-displays   Lista pantallas y salidas DXGI, y termina
          --verbose         Log detallado
          --help            Muestra esta ayuda
        """;

    public int Port { get; private set; } = Wire.DefaultPort;
    public ScreenMode Mode { get; private set; } = ScreenMode.Auto;
    /// <summary>Null: la última posición en la que el usuario dejó el monitor virtual.</summary>
    public DisplayPosition? Position { get; private set; }
    public int Monitor { get; private set; }
    public int Fps { get; private set; } = 60;
    public int BitrateMbps { get; private set; } = 12;
    public EncoderKind Encoder { get; private set; } = EncoderKind.Auto;
    public string FfmpegPath { get; private set; } = "ffmpeg";
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ListDisplays { get; private set; }

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        var options = new HostOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--port":
                    options.Port = ParseInt(arg, NextValue(args, ref i), 1, 65535);
                    break;
                case "--mode":
                    options.Mode = ParseEnum<ScreenMode>(arg, NextValue(args, ref i));
                    break;
                case "--position":
                    options.Position = ParseEnum<DisplayPosition>(arg, NextValue(args, ref i));
                    break;
                case "--monitor":
                    options.Monitor = ParseInt(arg, NextValue(args, ref i), 0, 15);
                    break;
                case "--fps":
                    options.Fps = ParseInt(arg, NextValue(args, ref i), 1, 240);
                    break;
                case "--bitrate":
                    options.BitrateMbps = ParseInt(arg, NextValue(args, ref i), 1, 200);
                    break;
                case "--encoder":
                    options.Encoder = ParseEnum<EncoderKind>(arg, NextValue(args, ref i));
                    break;
                case "--ffmpeg":
                    options.FfmpegPath = NextValue(args, ref i);
                    break;
                case "--list-displays":
                    options.ListDisplays = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Opción desconocida: {arg}");
            }
        }

        return options;
    }

    /// <summary>Convierte <see cref="ScreenMode.Auto"/> en extend o mirror según haya monitor virtual.</summary>
    public void ResolveMode(bool virtualDisplayAvailable)
    {
        if (Mode == ScreenMode.Auto) Mode = virtualDisplayAvailable ? ScreenMode.Extend : ScreenMode.Mirror;
    }

    private static string NextValue(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"Falta el valor para {args[i]}");
        return args[++i];
    }

    private static T ParseEnum<T>(string name, string value)
        where T : struct, Enum =>
        !int.TryParse(value, out _) && Enum.TryParse<T>(value, ignoreCase: true, out var result)
            ? result
            : throw new ArgumentException($"Valor inválido para {name}: {value}");

    private static int ParseInt(string name, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            || result < min || result > max)
        {
            throw new ArgumentException($"{name} debe ser un entero entre {min} y {max}");
        }

        return result;
    }
}
