namespace Seiko;

// `sk add` takes either a name or a local .sei path (ADR-0044). Classification
// is by shape, so a typo can never be read as the other kind: a separator or
// the .sei suffix wins, then an existing file of that exact name. Anything
// else is a name and goes through the normal resolver.
internal static class OriginPaths
{
    public const string Extension = ".sei";

    // The lock records `source = "origin"` for these entries; a configured
    // source of the same id would be indistinguishable from them.
    public const string SourceId = "origin";

    public static bool IsPathLike(string specification, string workingDirectory)
    {
        if (specification.Contains('/', StringComparison.Ordinal)
            || specification.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }

        if (specification.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return File.Exists(Path.Combine(workingDirectory, specification));
    }

    // Stored form is always relative to the install root, so a committed lock
    // survives a move to another machine or clone directory. Relative input is
    // resolved against the working directory first, then relativized; config
    // and lock only ever hold the result.
    public static string Normalize(string specification, string root, string workingDirectory)
    {
        // Only relative input is accepted; absolute input and file: URLs are
        // rejected rather than silently converted (ADR-0044).
        if (!IsStoredForm(specification))
        {
            var absolute = IsAbsolute(specification)
                ? Path.GetFullPath(specification)
                : Path.GetFullPath(Path.Combine(workingDirectory, specification));
            var converted = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            if (Path.IsPathRooted(converted))
            {
                throw new SeikoException(
                    $"{specification} is on another volume; move it under {root} and use a relative path",
                    "usage");
            }

            return converted;
        }

        var full = Path.GetFullPath(Path.Combine(workingDirectory, specification));

        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative))
        {
            // Different volume on Windows; there is no relative form to store.
            throw new SeikoException(
                $"{specification} is on another volume; move it under {root} and use a relative path",
                "usage");
        }

        return relative.Replace('\\', '/');
    }

    // Resolving is always relative to the install root, never to the working
    // directory, so a replay from anywhere behaves the same (ADR-0044).
    public static string Resolve(string stored, string root)
        => Path.GetFullPath(Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar)));

    // The file name stem must equal the sei name; hello-1.0.0.sei passes so
    // releases can carry their version in the name.
    public static bool StemMatches(string path, string name)
    {
        var file = Path.GetFileName(path);
        if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = file[..^Extension.Length];
        return string.Equals(stem, name, StringComparison.Ordinal)
            || stem.StartsWith(name + "-", StringComparison.Ordinal)
            || stem.StartsWith(name + "_", StringComparison.Ordinal)
            || stem.StartsWith(name + ".", StringComparison.Ordinal);
    }

    // Only a relative path is a stored form; absolute input and file: URLs
    // have to be converted, which Normalize does and the config loader checks.
    public static bool IsStoredForm(string value)
        => !Path.IsPathRooted(value)
            && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbsolute(string value)
        => !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Path.IsPathRooted(value);

    // The form a declaration and a lock must hold.
    public static void EnsureStored(string path, string what)
    {
        if (!IsStoredForm(path))
        {
            throw new SeikoException(
                $"{what} '{path}' must be relative to the install root",
                "lock");
        }
    }
}
