namespace Seiko;

internal static class Urls
{
    public static bool TryGetHttp(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    // Logs and errors may be persisted or handed to MCP callers. Drop userinfo,
    // query, and fragment so signed URLs and embedded credentials do not leak.
    public static string Redact(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
