using NexScreen.Core.Display;

namespace NexScreen.Core;

/// <summary>Preparación común de la consola y la app de bandeja antes de levantar el servidor.</summary>
internal static class HostStartup
{
    /// <summary>
    /// Verifica ffmpeg, informa el modo que se usaría ahora (el modo Auto se resuelve en cada
    /// conexión) y quita el monitor virtual si quedó en el escritorio. Devuelve un mensaje de error,
    /// o null si todo está bien.
    /// </summary>
    public static string? Prepare(HostOptions options)
    {
        if (!ToolLocator.Runs(options.FfmpegPath, "-version"))
        {
            return $"No se encontró ffmpeg ('{options.FfmpegPath}'). Instálalo con: winget install Gyan.FFmpeg";
        }

        // Solo informativo: en modo Auto el modo se vuelve a resolver en cada conexión (ClientSession).
        var driver = VirtualDisplayDriver.Check();
        var mode = options.ResolveMode(driver == DriverState.Working);
        if (mode != ScreenMode.Extend)
        {
            Log.Info(options.Mode == ScreenMode.Auto
                ? $"Modo espejo por ahora (no hay monitor virtual): se captura el monitor {options.Monitor}; se reevalúa en cada conexión"
                : $"Modo espejo: se captura el monitor {options.Monitor}");
            if (driver == DriverState.Broken) Log.Warn(BrokenDriverMessage);
            return null;
        }

        if (driver == DriverState.NotInstalled)
        {
            return "El modo extendido requiere el monitor virtual: ejecuta scripts\\install-virtual-display.ps1 como administrador";
        }

        if (driver == DriverState.Broken)
        {
            // Arrancar igual: así la bandeja puede ofrecer repararlo y cada conexión vuelve a comprobarlo.
            Log.Warn(BrokenDriverMessage);
            return null;
        }

        Log.Info(options.Position is { } position
            ? $"Modo extendido: la tablet será un monitor a la {PositionName(position)}"
            : "Modo extendido: la tablet será un monitor en la última posición usada (a la derecha la primera vez)");
        // Si el host terminó mal en una sesión anterior, el monitor virtual pudo quedar en el escritorio.
        VirtualDisplay.Detach(rememberPosition: false);
        return null;
    }

    public const string BrokenDriverMessage =
        "El driver del monitor virtual está instalado pero no responde (suele pasar tras actualizar Windows). " +
        "Usa \"Reparar monitor virtual\" en el menú de la bandeja";

    public static string PositionName(DisplayPosition position) => position switch
    {
        DisplayPosition.Right => "derecha",
        DisplayPosition.Left => "izquierda",
        DisplayPosition.Above => "arriba",
        _ => "abajo",
    };
}
