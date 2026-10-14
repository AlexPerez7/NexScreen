namespace NexScreen.Host;

internal static class Log
{
    private static readonly object Gate = new();

    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Write("DBG", message, ConsoleColor.DarkGray);
    }

    public static void Info(string message) => Write("INF", message, ConsoleColor.Gray);

    public static void Warn(string message) => Write("WRN", message, ConsoleColor.Yellow);

    public static void Error(string message) => Write("ERR", message, ConsoleColor.Red);

    private static void Write(string level, string message, ConsoleColor color)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {message}");
            Console.ResetColor();
        }
    }
}
