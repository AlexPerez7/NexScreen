using NexScreen.Core.Display;

namespace NexScreen.Core;

/// <summary>Preparación común de la consola y la app de bandeja antes de levantar el servidor.</summary>
internal static class HostStartup
{
    /// <summary>
    /// Verifica ffmpeg, resuelve el modo (extender si hay monitor virtual) y quita el monitor
    /// virtual si quedó en el escritorio. Devuelve un mensaje de error, o null si todo está bien.
    /// </summary>
    public static string? Prepare(HostOptions options)
    {
        if (!ToolLocator.Runs(options.FfmpegPath, "-version"))
        {
            return $"No se encontró ffmpeg ('{options.FfmpegPath}'). Instálalo con: winget install Gyan.FFmpeg";
        }

        var hasVirtualDisplay = VirtualDisplay.Find() is not null;
        options.ResolveMode(hasVirtualDisplay);
        if (options.Mode != ScreenMode.Extend)
        {
            Log.Info($"Modo espejo: se captura el monitor {options.Monitor}");
            return null;
        }

        if (!hasVirtualDisplay)
        {
            return "El modo extendido requiere el monitor virtual: ejecuta scripts\\install-virtual-display.ps1 como administrador";
        }

        Log.Info(options.Position is { } position
            ? $"Modo extendido: la tablet será un monitor a la {PositionName(position)}"
            : "Modo extendido: la tablet será un monitor en la última posición usada (a la derecha la primera vez)");
        // Si el host terminó mal en una sesión anterior, el monitor virtual pudo quedar en el escritorio.
        VirtualDisplay.Detach(rememberPosition: false);
        return null;
    }

    public static string PositionName(DisplayPosition position) => position switch
    {
        DisplayPosition.Right => "derecha",
        DisplayPosition.Left => "izquierda",
        DisplayPosition.Above => "arriba",
        _ => "abajo",
    };
}
