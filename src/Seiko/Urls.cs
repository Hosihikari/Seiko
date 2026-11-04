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
    // A URL is scanned to the end of its RFC 3986 character run, because
    // commas, semicolons and brackets are legal inside userinfo, queries and
    // IPv6 hosts. If the run does not parse, it is still stripped textually so
    // a malformed URL cannot persist credentials.
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
            while (end < text.Length && IsUriChar(text[end]))
            {
                end++;
            }

            var candidate = TrimTrailing(text[index..end], out var trimmed);
            builder.Append(
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? Redact(uri)
                    : StripTextually(candidate));
            builder.Append(trimmed);
            start = end;
        }
    }

    private static bool IsUriChar(char value)
        => char.IsAsciiLetterOrDigit(value)
            || (char.IsAscii(value) && "-._~:/?#[]@!$&'()*+,;=%".Contains(value, StringComparison.Ordinal));

    // Sentence punctuation commonly follows a URL. Only characters that are
    // rarely meaningful at the very end of a URL are trimmed, and never from
    // the inside of a bracket pair or a percent escape.
    private static string TrimTrailing(string candidate, out string trimmed)
    {
        var end = candidate.Length;
        while (end > 0 && candidate[end - 1] is '.' or ',' or ';' or ':' or '\'' or '"')
        {
            end--;
        }

        if (end > 0 && candidate[end - 1] == ')' && candidate.Count(c => c == '(') < candidate.Count(c => c == ')'))
        {
            end--;
        }

        if (end > 0 && candidate[end - 1] == ']' && candidate.Count(c => c == '[') < candidate.Count(c => c == ']'))
        {
            end--;
        }

        trimmed = candidate[end..];
        return candidate[..end];
    }

    // Last resort for a URL the parser rejected: drop userinfo, query and
    // fragment textually so the credential part is gone even then.
    private static string StripTextually(string candidate)
    {
        var cut = candidate.IndexOfAny(['?', '#']);
        var visible = cut < 0 ? candidate : candidate[..cut];
        var scheme = visible.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var authorityEnd = visible.IndexOf('/', scheme + 3);
            var authority = authorityEnd < 0 ? visible : visible[..authorityEnd];
            var at = authority.LastIndexOf('@');
            if (at > scheme + 2)
            {
                visible = visible[..(scheme + 3)] + visible[(at + 1)..];
            }
        }

        return visible;
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
