namespace Sanabi.Misc;

/// <summary>
///     Logging API for SanabiLauncher.
/// </summary>
public static class SanabiLogger
{
    public static void ColoredWrite(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(message);
        Console.ResetColor();
    }

    public static void Log(string message)
    {
        Console.WriteLine($"[SANABI]: {message}");
    }

    public static void LogInfo(string message)
    {
        ColoredWrite("[SANABI-INFO]", ConsoleColor.DarkBlue);
        Console.WriteLine($" {message}");
    }

    public static void LogWarn(string message)
    {
        ColoredWrite("[SANABI-WARN]", ConsoleColor.Yellow);
        Console.WriteLine($" {message}");
    }

    public static void LogError(string message)
    {
        ColoredWrite("[SANABI-ERRO]", ConsoleColor.Red);
        Console.WriteLine($" {message}");
    }

    public static void LogFatal(string message)
    {
        ColoredWrite("[SANABI-FATL]", ConsoleColor.Magenta);
        Console.WriteLine($" {message}");
    }
}
