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

    // Text that already contains URLs: redact every http(s) URL in place.
    public static string RedactText(string text)
    {
        var start = 0;
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var http = text.IndexOf("http://", start, StringComparison.OrdinalIgnoreCase);
            var https = text.IndexOf("https://", start, StringComparison.OrdinalIgnoreCase);
            var index = http < 0 ? https : https < 0 ? http : Math.Min(http, https);
            if (index < 0)
            {
                builder.Append(text, start, text.Length - start);
                return builder.ToString();
            }

            builder.Append(text, start, index - start);
            var end = index;
            while (end < text.Length
                && !char.IsWhiteSpace(text[end])
                && !IsUrlTerminator(text[end]))
            {
                end++;
            }

            var candidate = text[index..end];
            builder.Append(
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? Redact(uri)
                    : candidate);
            start = end;
        }
    }

    private static bool IsUrlTerminator(char value)
        => value == '"' || value == (char)39 || value is ')' or ']' or '>' or ',' or ';';

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
