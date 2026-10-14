namespace NexScreen.Core;

/// <summary>
/// Log del host: consola (si hay), archivo opcional y un evento para la interfaz.
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static bool Verbose { get; set; }

    /// <summary>Si está definido, cada línea se agrega al archivo (rota a .old al pasar 5 MB).</summary>
    public static string? FilePath { get; set; }

    /// <summary>Escribe en la consola. La app de bandeja no tiene consola y lo desactiva.</summary>
    public static bool ConsoleEnabled { get; set; } = true;

    /// <summary>Nivel ("INF", "WRN"...) y mensaje. Se dispara desde el hilo que registra.</summary>
    public static event Action<string, string>? Written;

    public static void Debug(string message)
    {
        if (Verbose) Write("DBG", message, ConsoleColor.DarkGray);
    }

    public static void Info(string message) => Write("INF", message, ConsoleColor.Gray);

    public static void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            if (ConsoleEnabled)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(line[11..]); // en consola alcanza con la hora
                Console.ResetColor();
            }

            if (FilePath is not null) AppendToFile(FilePath, line);
        }

        Written?.Invoke(level, message);
    }

    private static void AppendToFile(string path, string line)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxFileBytes) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un log que no se puede escribir no debe tumbar el host.
        }
    }
}
