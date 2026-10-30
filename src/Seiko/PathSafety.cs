namespace Seiko;

// Lexical containment is not enough: a directory symlink inside the install
// root redirects writes and deletes outside it. Every managed path is walked
// component by component and rejected if any existing component is a link.
internal static class PathSafety
{
    public static void EnsureNoLinks(string root, string relative, string code = "deploy")
    {
        var normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new SeikoException($"invalid deployment path: {relative}", code);
        }

        var current = Path.GetFullPath(root);
        foreach (var segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
            {
                throw new SeikoException(
                    $"deployment path escapes the install root through a link: {relative}",
                    code);
            }
        }
    }

    public static bool IsLink(string path)
    {
        if (TryTarget(() => new FileInfo(path).LinkTarget))
        {
            return true;
        }

        if (TryTarget(() => new DirectoryInfo(path).LinkTarget))
        {
            return true;
        }

        try
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
        }

        return false;
    }

    private static bool TryTarget(Func<string?> target)
    {
        try
        {
            return target() is not null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
