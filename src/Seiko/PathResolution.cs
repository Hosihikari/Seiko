namespace Seiko;

// A relative path that has already passed the lexical checks. Keeping the
// validated form in a type prevents later code from re-parsing a raw value
// with a different rule set.
internal readonly struct RelativePath
{
    private RelativePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    private static readonly char[] WindowsInvalidFileNameChars = Path
        .GetInvalidFileNameChars()
        .Where(character => character is not ('/' or '\\'))
        .ToArray();

    public static RelativePath Parse(string value, string code = "deploy")
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Contains('\0')
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new SeikoException($"invalid path: {value}", code);
        }

        return new RelativePath(normalized);
    }

    // Stricter form used for stored metadata: a backslash is rejected instead
    // of normalized, and Windows-invalid file names are refused up front.
    public static bool IsSafeStrict(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains('\0')
            || value.Contains('\\')
            || Path.IsPathRooted(value))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && value.IndexOfAny(WindowsInvalidFileNameChars) >= 0)
        {
            return false;
        }

        return !value.Split('/').Any(segment => segment is "" or "." or "..");
    }

    public override string ToString() => Value;
}

internal static class RootedPath
{
    // Resolution owns the link check: a lexical containment test alone would
    // still follow a linked parent directory.
    public static string Resolve(string root, RelativePath relative, string code = "deploy")
    {
        PathSafety.EnsureNoLinks(root, relative.Value, code);
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(
            Path.Combine(rootFull, relative.Value.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SeikoException($"path escapes the root: {relative}", code);
        }

        return full;
    }

    public static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    public static bool IsUnderAny(string path, IEnumerable<string> roots)
        => roots.Any(root => IsUnder(path, root));
}
