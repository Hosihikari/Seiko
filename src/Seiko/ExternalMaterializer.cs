using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Seiko;

// Materializes declared external bytes into the artifact staging directory so
// the finished store entry is self-contained: the normal per-file digest
// verify, deploy, rollback and offline paths keep working unchanged.
internal static class ExternalMaterializer
{
    public static void Run(
        ResolvedSei sei,
        SeiManifest manifest,
        string directory,
        List<StoreFileDigest> digests,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var artifact = manifest.Artifact!;
        var files = artifact.ExternalFiles ?? [];
        var archives = artifact.ExternalArchives ?? [];
        if (files.Count == 0 && archives.Count == 0)
        {
            return;
        }

        // A local source is a directory copy and never fetches; keep the same
        // rule the source layer already applies to hosted artifact URLs.
        if (sei.Source is LocalSource)
        {
            throw new SeikoException(
                $"{sei.Name}: a local source cannot serve external files",
                "source");
        }

        var staging = Path.Combine(directory, ".external");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = new ArtifactLocation { Url = file.Url, Sha256 = file.Sha256, Size = file.Size };
                var slot = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(slot);
                session.Log?.Invoke($"{sei.Name}: fetching external file {file.Path}");
                sei.Source.DownloadAsync(sei.Name, location, slot, session, cancellationToken)
                    .GetAwaiter().GetResult();

                var downloaded = Path.Combine(slot, "artifact");
                Verify(downloaded, file.Sha256, file.Size, sei.Name, file.Path);
                var destination = Path.Combine(directory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(downloaded, destination, overwrite: true);
                digests.Add(new StoreFileDigest
                {
                    Path = file.Path,
                    Sha256 = file.Sha256,
                    Size = new FileInfo(destination).Length,
                });
                Delete(slot);
            }

            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var archive in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(slot);
                if (done.Add(archive.Url))
                {
                    var location = new ArtifactLocation
                    {
                        Url = archive.Url,
                        Sha256 = archive.Sha256,
                        Size = archive.Size,
                    };
                    session.Log?.Invoke($"{sei.Name}: fetching external archive {archive.Url}");
                    sei.Source.DownloadAsync(sei.Name, location, slot, session, cancellationToken)
                        .GetAwaiter().GetResult();
                    var downloaded = Path.Combine(slot, "artifact");
                    Verify(downloaded, archive.Sha256, archive.Size, sei.Name, archive.Url);
                    ExtractMembers(downloaded, archive, directory, sei, digests, cancellationToken);
                }
                else
                {
                    // Same URL and digest: targets were already written, so
                    // only re-record them instead of downloading again.
                    foreach (var member in archive.Members)
                    {
                        var source = Path.Combine(directory, member.Path);
                        digests.Add(new StoreFileDigest
                        {
                            Path = member.Path,
                            Sha256 = Hashing.Sha256File(source),
                            Size = new FileInfo(source).Length,
                        });
                    }
                }

                Delete(slot);
            }
        }
        finally
        {
            Delete(staging);
        }
    }

    private static void ExtractMembers(
        string downloaded,
        ExternalArchive archive,
        string directory,
        ResolvedSei sei,
        List<StoreFileDigest> digests,
        CancellationToken cancellationToken)
    {
        var wanted = archive.Members
            .GroupBy(member => ExternalFiles.NormalizeMember(member.Member), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, bytes) in ReadArchive(downloaded, archive.Format, cancellationToken))
        {
            if (!wanted.TryGetValue(name, out var targets))
            {
                continue;
            }

            seen.Add(name);
            foreach (var target in targets)
            {
                digests.Add(WriteTarget(directory, target.Path, bytes, sei, target.Member));
            }
        }

        foreach (var name in wanted.Keys)
        {
            if (!seen.Contains(name))
            {
                throw new SeikoException(
                    $"{sei.Name}: external archive does not contain '{name}'",
                    "checksum");
            }
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> ReadArchive(
        string path,
        string format,
        CancellationToken cancellationToken)
    {
        if (format == "zip")
        {
            using var archive = ZipFile.OpenRead(path);
            ArchiveLimits.EnsureEntryCount(archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                var name = ExternalFiles.NormalizeMember(entry.FullName);
                if (name.Length == 0 || name.EndsWith('/'))
                {
                    continue;
                }

                ArchiveLimits.EnsureEntrySize(entry.Length);
                using var stream = entry.Open();
                yield return (name, ReadAll(stream, cancellationToken));
            }

            yield break;
        }

        using var file = File.OpenRead(path);
        using Stream tarStream = format == "tar.gz"
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new TarReader(tarStream);
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null)
            {
                continue;
            }

            var name = ExternalFiles.NormalizeMember(entry.Name);
            if (name.Length == 0)
            {
                continue;
            }

            yield return (name, ReadAll(entry.DataStream, cancellationToken));
        }
    }

    private static byte[] ReadAll(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            ArchiveLimits.EnsureEntrySize(total);
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static StoreFileDigest WriteTarget(
        string directory,
        string path,
        byte[] bytes,
        ResolvedSei sei,
        string member)
    {
        if (bytes.Length > ArchiveLimits.MaxEntryBytes)
        {
            throw new SeikoException(
                $"{sei.Name}: external member '{member}' exceeds the {ArchiveLimits.MaxEntryBytes}-byte limit",
                "checksum");
        }

        var destination = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
        return new StoreFileDigest
        {
            Path = path,
            Sha256 = Hashing.Sha256Bytes(bytes),
            Size = bytes.Length,
        };
    }

    private static void Verify(string path, string sha256, long size, string name, string label)
    {
        var length = new FileInfo(path).Length;
        if (size > 0 && length != size)
        {
            throw new SeikoException(
                $"{name}: external size mismatch for {label}: expected {size}, got {length}",
                "checksum");
        }

        var actual = Hashing.Sha256File(path);
        if (!string.Equals(actual, sha256.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new SeikoException(
                $"{name}: external checksum mismatch for {label}: expected {sha256}, got {actual}",
                "checksum");
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
