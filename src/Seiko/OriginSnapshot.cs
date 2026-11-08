namespace Seiko;

// Origin files are copied into the store when they are added, so a deleted or
// rebuilt original cannot take a granted generation away with it (ADR-0044).
// Snapshots are immutable and live outside the content-addressed tree; the
// artifact lock and GC treat them like any other stored artifact.
internal static class OriginSnapshot
{
    public const string FolderName = "origin";

    public static string Folder(string storeRoot) => Path.Combine(storeRoot, FolderName);

    public static string SnapshotPath(string storeRoot, string name, string sha256)
        => Path.Combine(Folder(storeRoot), FileName(name, sha256));

    public static string FileName(string name, string sha256)
        => $"{name}-{Hashing.NormalizeSha256(sha256)}.sei";

    // Returns the stored snapshot path, copying the source when it is not
    // already there. The digest is verified before the copy is published.
    public static string Capture(string storeRoot, string name, string sourcePath, string sha256)
    {
        var expected = Hashing.NormalizeSha256(sha256);
        var directory = Folder(storeRoot);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, FileName(name, expected));
        if (File.Exists(destination))
        {
            return destination;
        }

        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            var actual = Hashing.Sha256File(temp);
            if (!actual.Equals(expected, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"{Path.GetFileName(sourcePath)} changed while it was being added "
                    + $"({expected} expected, {actual} read)",
                    "checksum");
            }

            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return destination;
    }
}
