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

    public static RelativePath Parse(string value, string code = "deploy")
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new SeikoException($"invalid path: {value}", code);
        }

        return new RelativePath(normalized);
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
}
