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
        string stagingRoot,
        string artifactSha,
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

        // The artifact content tree may legitimately contain ".external/...";
        // staging inside it would let the cleanup delete real files. The name
        // carries the artifact digest so StoreStaging can reclaim it under the
        // same lock if the process dies before the finally (ADR-0005,
        // ADR-0024).
        var staging = Path.Combine(
            stagingRoot,
            artifactSha + ".ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var budget = new PackageBudget();
        try
        {
            // One URL is downloaded once (ADR-0032), even when it feeds more
            // than one target through repeated declarations.
            var fileGroups = new Dictionary<string, List<ExternalFile>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!fileGroups.TryGetValue(file.Url, out var group))
                {
                    group = [];
                    fileGroups[file.Url] = group;
                }

                group.Add(file);
            }

            foreach (var (url, group) in fileGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = group[0];
                var location = new ArtifactLocation
                {
                    Url = url,
                    Sha256 = identity.Sha256,
                    Size = identity.Size,
                };
                var slot = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(slot);
                session.Log?.Invoke($"{sei.Name}: fetching external file {url}");
                sei.Source.DownloadAsync(sei.Name, location, slot, session, cancellationToken)
                    .GetAwaiter().GetResult();

                var downloaded = Path.Combine(slot, "artifact");
                Verify(downloaded, identity.Sha256, identity.Size, sei.Name, url, ArchiveLimits.MaxEntryBytes);
                var length = new FileInfo(downloaded).Length;
                budget.AddDownload(length);
                var firstDestination = Path.Combine(directory, group[0].Path);
                Directory.CreateDirectory(Path.GetDirectoryName(firstDestination)!);
                File.Move(downloaded, firstDestination, overwrite: true);
                for (var index = 0; index < group.Count; index++)
                {
                    var destination = Path.Combine(directory, group[index].Path);
                    if (index > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(firstDestination, destination);
                    }

                    budget.AddWritten(length);
                    digests.Add(new StoreFileDigest
                    {
                        Path = group[index].Path,
                        Sha256 = identity.Sha256,
                        Size = length,
                    });
                }

                Delete(slot);
            }

            // Repeated declarations of one URL describe the same download with
            // more members (ADR-0032: one archive is downloaded once), so merge
            // them before fetching.
            var groups = new Dictionary<string, List<ExternalArchiveMember>>(StringComparer.Ordinal);
            var identities = new Dictionary<string, ExternalArchive>(StringComparer.Ordinal);
            foreach (var archive in archives)
            {
                if (!groups.TryGetValue(archive.Url, out var members))
                {
                    members = [];
                    groups[archive.Url] = members;
                    identities[archive.Url] = archive;
                }

                members.AddRange(archive.Members);
            }

            foreach (var (url, members) in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = identities[url];
                var slot = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(slot);
                var location = new ArtifactLocation
                {
                    Url = identity.Url,
                    Sha256 = identity.Sha256,
                    Size = identity.Size,
                };
                session.Log?.Invoke($"{sei.Name}: fetching external archive {identity.Url}");
                sei.Source.DownloadAsync(sei.Name, location, slot, session, cancellationToken)
                    .GetAwaiter().GetResult();
                var downloaded = Path.Combine(slot, "artifact");
                Verify(
                    downloaded,
                    identity.Sha256,
                    identity.Size,
                    sei.Name,
                    identity.Url,
                    ArchiveLimits.MaxTotalBytes);
                budget.AddDownload(new FileInfo(downloaded).Length);

                var merged = new ExternalArchive
                {
                    Url = identity.Url,
                    Sha256 = identity.Sha256,
                    Format = identity.Format,
                    Size = identity.Size,
                    Members = members,
                };
                ExtractMembers(downloaded, merged, directory, sei, digests, budget, cancellationToken);
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
        PackageBudget budget,
        CancellationToken cancellationToken)
    {
        var wanted = archive.Members
            .GroupBy(member => ExternalFiles.NormalizeMember(member.Member), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(member => member.Path).ToList(),
                StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = new ArchiveReadState();

        if (archive.Format == "zip")
        {
            using var zip = ZipFile.OpenRead(downloaded);
            ArchiveLimits.EnsureEntryCount(zip.Entries.Count);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.AddEntries(1);
                var name = ExternalFiles.NormalizeMember(entry.FullName);
                if (name.Length == 0 || name.EndsWith('/'))
                {
                    continue;
                }

                EnsureUniqueMember(state, name, sei);
                ArchiveLimits.EnsureEntrySize(entry.Length);
                state.Declared += entry.Length;
                ArchiveLimits.EnsureTotal(state.Declared);
                if (!wanted.TryGetValue(name, out var targets))
                {
                    continue;
                }

                seen.Add(name);
                using var input = entry.Open();
                WriteTargets(input, targets, directory, digests, state, budget, cancellationToken);
            }
        }
        else
        {
            using var file = File.OpenRead(downloaded);
            using Stream tarStream = archive.Format == "tar.gz"
                ? new GZipStream(file, CompressionMode.Decompress)
                : file;
            using var reader = new TarReader(tarStream);
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                state.Count++;
                ArchiveLimits.EnsureEntryCount(state.Count);
                budget.AddEntries(1);

                var name = ExternalFiles.NormalizeMember(entry.Name);
                if (name.Length > 0)
                {
                    EnsureUniqueMember(state, name, sei);
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    // Directories, links and other non-files are never
                    // materialized; the next GetNextEntry advances past them.
                    continue;
                }

                ArchiveLimits.EnsureEntrySize(entry.Length);
                state.Declared += entry.Length;
                ArchiveLimits.EnsureTotal(state.Declared);
                if (name.Length == 0)
                {
                    continue;
                }

                // TarReader reports a null DataStream for a legal zero-length
                // regular file; it is an empty member, not a missing one.
                var data = entry.DataStream;
                if (data is null)
                {
                    if (entry.Length != 0)
                    {
                        continue;
                    }

                    data = Stream.Null;
                }

                if (wanted.TryGetValue(name, out var targets))
                {
                    seen.Add(name);
                    WriteTargets(data, targets, directory, digests, state, budget, cancellationToken);
                }
                else if (!ReferenceEquals(data, Stream.Null))
                {
                    // Skipped members still consume decompression budget; a
                    // tar.gz cannot seek past them for free.
                    Drain(data, state, cancellationToken);
                }
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

    private static void EnsureUniqueMember(ArchiveReadState state, string name, ResolvedSei sei)
    {
        if (!state.Names.Add(name))
        {
            throw new SeikoException(
                $"{sei.Name}: external archive contains duplicate member '{name}'",
                "checksum");
        }
    }

    private static void WriteTargets(
        Stream input,
        IReadOnlyList<string> targets,
        string directory,
        List<StoreFileDigest> digests,
        ArchiveReadState state,
        PackageBudget budget,
        CancellationToken cancellationToken)
    {
        var firstDestination = Path.Combine(directory, targets[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(firstDestination)!);

        long size = 0;
        string sha256;
        using (var output = new FileStream(
            firstDestination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                size += read;
                ArchiveLimits.EnsureEntrySize(size);
                state.Written += read;
                ArchiveLimits.EnsureTotal(state.Written);
            }

            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        // One member may feed several targets; copy the extracted file rather
        // than holding the whole member in memory.
        for (var index = 0; index < targets.Count; index++)
        {
            if (index > 0)
            {
                var destination = Path.Combine(directory, targets[index]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(firstDestination, destination);
            }

            // Copying one member to several targets is write amplification and
            // counts against the package budget each time.
            budget.AddWritten(size);
            digests.Add(new StoreFileDigest
            {
                Path = targets[index],
                Sha256 = sha256,
                Size = size,
            });
        }
    }

    private static void Drain(Stream stream, ArchiveReadState state, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Written += read;
            ArchiveLimits.EnsureTotal(state.Written);
        }
    }

    private sealed class ArchiveReadState
    {
        public int Count { get; set; }
        public long Declared { get; set; }
        public long Written { get; set; }
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    // Per-package accounting so many individually valid declarations cannot
    // add up past the archive budgets (ADR-0024, ADR-0032).
    private sealed class PackageBudget
    {
        public long Downloaded { get; private set; }
        public long Written { get; private set; }
        public int Entries { get; private set; }

        public void AddDownload(long bytes)
        {
            Downloaded += bytes;
            ArchiveLimits.EnsureTotal(Downloaded);
        }

        public void AddWritten(long bytes)
        {
            Written += bytes;
            ArchiveLimits.EnsureTotal(Written);
        }

        public void AddEntries(int count)
        {
            Entries += count;
            ArchiveLimits.EnsureEntryCount(Entries);
        }
    }

    private static void Verify(
        string path,
        string sha256,
        long size,
        string name,
        string label,
        long maxBytes)
    {
        var length = new FileInfo(path).Length;
        if (length > maxBytes)
        {
            throw new SeikoException(
                $"{name}: external payload '{label}' is {length} bytes; the limit is {maxBytes}",
                "checksum");
        }

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
