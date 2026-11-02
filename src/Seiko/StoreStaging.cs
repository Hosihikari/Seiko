namespace Seiko;

// Store extraction writes into <sha256>.tmp-<guid> and moves the directory
// into place. A killed process leaves that staging directory behind, so cache
// maintenance needs to recognize the exact shape and only remove it while the
// artifact lock proves no extraction is still using it.
internal static class StoreStaging
{
    private const int HashLength = 64;
    private const int GuidLength = 32;
    private const string Marker = ".tmp-";

    public static bool IsStagingName(string name, out string artifact)
    {
        artifact = "";
        if (name.Length != HashLength + Marker.Length + GuidLength
            || !IsLowerHex(name.AsSpan(0, HashLength))
            || !name.AsSpan(HashLength, Marker.Length).SequenceEqual(Marker)
            || !IsLowerHex(name.AsSpan(HashLength + Marker.Length)))
        {
            return false;
        }

        artifact = name[..HashLength];
        return true;
    }

    public static IEnumerable<string> Enumerate(string storePath)
    {
        if (!Directory.Exists(storePath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(storePath))
        {
            if (IsStagingName(Path.GetFileName(directory), out _))
            {
                yield return directory;
            }
        }
    }

    public static ProcessLock? TryLock(string storePath, string artifact)
        => ProcessLock.TryAcquire(
            Path.Combine(storePath, ".locks", artifact + ".lock"));

    public static bool TryDelete(string storePath, string directory)
    {
        if (!IsStagingName(Path.GetFileName(directory), out var artifact))
        {
            return false;
        }

        using var artifactLock = TryLock(storePath, artifact);
        if (artifactLock is null || !Directory.Exists(directory))
        {
            return false;
        }

        Directory.Delete(directory, recursive: true);
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
