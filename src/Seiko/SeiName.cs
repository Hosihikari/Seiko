namespace Seiko;

internal static class SeiName
{
    // Names become file names under sei/ in a source, so reject anything
    // that could escape that directory or is not portable across platforms.
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return false;
        }

        if (name[0] is ' ' or '.' || name[^1] is ' ' or '.')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < ' ' || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        return true;
    }
}
