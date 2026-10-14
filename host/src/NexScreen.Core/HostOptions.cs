using System.Globalization;
using NexScreen.Core.Display;
using NexScreen.Core.Protocol;

namespace NexScreen.Core;

internal enum EncoderKind
{
    Auto,

    /// <summary>Intel Quick Sync.</summary>
    Qsv,

    /// <summary>NVIDIA.</summary>
    Nvenc,

    /// <summary>AMD.</summary>
    Amf,

    /// <summary>Media Foundation con el encoder de hardware que instaló el driver de la GPU (cualquier fabricante).</summary>
    MfGpu,

    /// <summary>Media Foundation por software: viene con Windows, está siempre disponible.</summary>
    Mf,

    /// <summary>Solo si el ffmpeg incluye libx264 (las builds GPL; el instalador trae una LGPL).</summary>
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
          --fps <n>         Cuadros por segundo (por defecto, la frecuencia de la pantalla del
                            dispositivo: 60, 90, 120...; hasta 60 con un encoder por CPU)
          --bitrate <n>     Bitrate máximo en Mbps (por defecto 12 para 2304x1440 a 60 fps y
                            proporcional a la resolución y los fps del dispositivo). Si la red no
                            da abasto, se baja solo y vuelve a subir cuando la red se recupera
          --fixed-bitrate   No adaptar el bitrate a la red
          --encoder <e>     auto | qsv | nvenc | amf | mfgpu | mf | x264 (por defecto auto: el de
                            la GPU de la pantalla, si no Media Foundation por GPU, si no por CPU)
          --ffmpeg <ruta>   Ruta a ffmpeg.exe (por defecto se busca en PATH)
          --scale <n>       Escala de Windows para el dispositivo al extender: auto (según la
                            densidad de su pantalla) o 100, 125, 150, 175, 200... (por defecto no se cambia)
          --no-usb          No detectar dispositivos por USB (por defecto arma el túnel y abre la app sola)
          --list-displays   Lista pantallas y salidas DXGI, y termina
          --verbose         Log detallado
          --help            Muestra esta ayuda
        """;

    public int Port { get; set; } = Wire.DefaultPort;
    public ScreenMode Mode { get; set; } = ScreenMode.Auto;
    /// <summary>Null: la última posición en la que el usuario dejó el monitor virtual.</summary>
    public DisplayPosition? Position { get; set; }
    public int Monitor { get; set; }
    /// <summary>Null: la frecuencia de la pantalla del dispositivo (ver <see cref="Video.StreamTuning.ChooseFps"/>).</summary>
    public int? Fps { get; set; }

    /// <summary>
    /// Bitrate máximo. Con <see cref="ScaleBitrate"/> es la referencia para 2304×1440 a 60 fps y se
    /// ajusta a la resolución y los fps del dispositivo; si no, es el valor exacto.
    /// </summary>
    public int BitrateMbps { get; set; } = 12;

    public bool ScaleBitrate { get; set; } = true;

    /// <summary>Bajar el bitrate si la red se congestiona (<see cref="BitrateMbps"/> es el máximo).</summary>
    public bool AdaptiveBitrate { get; set; } = true;
    public EncoderKind Encoder { get; set; } = EncoderKind.Auto;
    public string FfmpegPath { get; set; } = ToolLocator.Find("ffmpeg");
    public bool Verbose { get; set; }
    public bool ShowHelp { get; set; }
    public bool ListDisplays { get; set; }

    /// <summary>Escala de Windows para el monitor del dispositivo (100, 125, 150...). Null: no tocarla.</summary>
    public int? ScalePercent { get; set; }

    /// <summary>Elegir la escala según la densidad de la pantalla del dispositivo (gana sobre <see cref="ScalePercent"/>).</summary>
    public bool AutoScale { get; set; }

    /// <summary>Detectar dispositivos por USB, armar el túnel y abrir la app (requiere adb).</summary>
    public bool UsbAutoConnect { get; set; } = true;

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
                    options.ScaleBitrate = false;
                    break;
                case "--fixed-bitrate":
                    options.AdaptiveBitrate = false;
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
                case "--scale":
                    var scale = NextValue(args, ref i);
                    if (string.Equals(scale, "auto", StringComparison.OrdinalIgnoreCase)) options.AutoScale = true;
                    else options.ScalePercent = ParseInt(arg, scale, 100, 500);
                    break;
                case "--no-usb":
                    options.UsbAutoConnect = false;
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
