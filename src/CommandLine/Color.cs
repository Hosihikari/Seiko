namespace Seiko.CommandLine;

// Colour is emphasis only: every coloured state also carries a symbol or word,
// so a pipe, a dumb terminal or a colour-blind reader loses nothing.
internal static class Color
{
    private static bool _enabled;

    public static void Configure(string? mode)
    {
        _enabled = mode switch
        {
            "always" => true,
            "never" => false,
            _ => !Console.IsOutputRedirected
                && !Console.IsErrorRedirected
                && Environment.GetEnvironmentVariable("NO_COLOR") is null or "",
        };
    }

    public static string Error(string text) => Wrap(text, "\u001b[31m");

    public static string Warning(string text) => Wrap(text, "\u001b[33m");

    public static string Success(string text) => Wrap(text, "\u001b[32m");

    public static string Dim(string text) => Wrap(text, "\u001b[2m");

    private static string Wrap(string text, string code)
        => _enabled && text.Length > 0 ? code + text + "\u001b[0m" : text;
}
