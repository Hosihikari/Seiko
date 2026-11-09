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

    // Only a relative input is accepted, resolved against the working
    // directory; the stored form is always relative to the install root, so a
    // committed lock survives a move to another machine or clone directory.
    public static string Normalize(string specification, string root, string workingDirectory)
    {
        // A leading `./` is a spelling, not a location: `./C:\x` is still the
        // absolute path C:\x, and combining it blindly would bury the drive
        // inside a relative path. Strip the prefixes before judging the shape.
        var trimmed = specification;
        while (trimmed.StartsWith("./", StringComparison.Ordinal)
            || trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        // An absolute path or a file: URL is rejected, not converted: the
        // relative form is the one contract and input keeps the same shape
        // (ADR-0044).
        if (!IsStoredForm(trimmed))
        {
            throw new SeikoException(
                $"{specification} must be relative to the install root, "
                + "or to the working directory when adding it",
                "usage");
        }

        var full = Path.GetFullPath(Path.Combine(workingDirectory, trimmed));

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

    // The only accepted shape, for input, config and lock alike.
    public static bool IsStoredForm(string value)
        => !Path.IsPathRooted(value)
            && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    // Whether two stored paths name the same file. Both are compared after
    // resolution; the filesystem decides case, not the OS name: a differently
    // cased spelling only adopts the entry's own spelling where the volume is
    // case-insensitive, so a case-sensitive APFS volume keeps two distinct
    // files apart and symlink components fold (ADR-0044).
    public static bool SameFile(string left, string right, string root)
        => string.Equals(
            RootRegistry.Resolve(Resolve(left, root)),
            RootRegistry.Resolve(Resolve(right, root)),
            StringComparison.Ordinal);

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
