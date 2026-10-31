namespace Seiko;

internal static class Urls
{
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
