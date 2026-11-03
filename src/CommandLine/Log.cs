namespace Seiko.CommandLine;

internal static class Log
{
    public static bool Verbose { get; set; }
    public static bool Quiet { get; set; }

    public static void Debug(string message)
    {
        if (Verbose && !Quiet)
        {
            Console.Error.WriteLine($"sk: {message}");
        }
    }

    public static void Root(string path)
    {
        if (!Quiet)
        {
            Console.Error.WriteLine($"sk: root {path}");
        }
    }

    public static void Warning(string message)
    {
        if (!Quiet)
        {
            Console.Error.WriteLine($"sk: {message}");
        }
    }

    public static void Error(string message)
    {
        Console.Error.WriteLine($"sk: {message}");
    }
}
