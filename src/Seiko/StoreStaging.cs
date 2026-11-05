namespace Seiko;

// Store extraction writes into <sha256>.tmp-<guid> and moves the directory
// into place. A killed process leaves that staging directory behind, so cache
// maintenance needs to recognize the exact shape and only remove it while the
// artifact lock proves no extraction is still using it.
internal static class StoreStaging
{
    private const int HashLength = 64;
    private const int GuidLength = 32;
    private const int MarkerLength = 5;

    // Both artifact extraction and external downloads stage on the store root
    // under the owning artifact's digest, so an interrupted process leaves a
    // directory that doctor/cache can reclaim under the artifact lock.
    public static bool IsStagingName(string name, out string artifact)
    {
        artifact = "";
        if (name.Length != HashLength + MarkerLength + GuidLength
            || !Hashing.IsLowerHex(name.AsSpan(0, HashLength)))
        {
            return false;
        }

        var marker = name.AsSpan(HashLength, MarkerLength);
        if (!marker.SequenceEqual(".tmp-") && !marker.SequenceEqual(".ext-"))
        {
            return false;
        }

        if (!Hashing.IsLowerHex(name.AsSpan(HashLength + MarkerLength)))
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

}
