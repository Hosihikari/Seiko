using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Seiko;

public sealed record StoredSei(
    string Directory,
    SeiManifest Manifest,
    IReadOnlyDictionary<string, StoreFileDigest> Digests);

public sealed class Store
{
    public string Root { get; }

    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public StoredSei? TryGet(string sha256)
    {
        sha256 = Hashing.NormalizeSha256(sha256);
        using var artifactLock = ProcessLock.AcquireWaiting(
            Path.Combine(Root, ".locks", sha256 + ".lock"),
            $"artifact {sha256}");
        var directory = Path.Combine(Root, sha256);
        var manifestPath = Path.Combine(directory, "sei.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var digests = LoadDigests(sha256, directory);
        VerifyFiles(directory, digests);
        var manifest = ReadManifest(directory, manifestPath);
        return new StoredSei(directory, manifest, digests);
    }

    public StoredSei Ensure(ResolvedSei sei, DownloadSession session, CancellationToken cancellationToken)
        => EnsureAsync(sei, session, cancellationToken).GetAwaiter().GetResult();

    public async Task<StoredSei> EnsureAsync(
        ResolvedSei sei,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var expectedSha = Hashing.NormalizeSha256(sei.Artifact.Source.Sha256);
        var directory = Path.Combine(Root, expectedSha);
        var manifestPath = Path.Combine(directory, "sei.json");
        var partialRoot = Path.Combine(Root, ".partial");
        Directory.CreateDirectory(partialRoot);
        var partialDirectory = Path.Combine(partialRoot, expectedSha);

        // Hold the artifact lock for the read as well: another process may be
        // replacing the directory, and a lock-free probe can observe the gap.
        using var artifactLock = ProcessLock.AcquireWaiting(
            Path.Combine(Root, ".locks", expectedSha + ".lock"),
            $"artifact {expectedSha}",
            cancellationToken: cancellationToken);

        if (File.Exists(manifestPath))
        {
            try
            {
                var stored = LoadStored(sei, directory, manifestPath);
                session.ReportCompleted(sei.Name, sei.Artifact.Source.Size);
                return stored;
            }
            catch (SeikoException ex) when (ex.Code == "cache-repair")
            {
                // The artifact directory is replaced below.
            }
        }

        Directory.CreateDirectory(partialDirectory);
        try
        {
            await sei.Source.DownloadAsync(
                    sei.Name,
                    sei.Artifact.Source,
                    partialDirectory,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var artifactPath = Path.Combine(partialDirectory, "artifact");
            List<StoreFileDigest> extracted;
            await session.ExtractSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var length = new FileInfo(artifactPath).Length;
                if (sei.Artifact.Source.Size > 0 && length != sei.Artifact.Source.Size)
                {
                    throw new SeikoException(
                        $"size mismatch for {sei.Name} {sei.Version}: expected {sei.Artifact.Source.Size}, got {length}",
                        "checksum");
                }

                var actual = Hashing.Sha256File(artifactPath);
                if (!actual.Equals(expectedSha, StringComparison.Ordinal))
                {
                    throw new SeikoException(
                        $"checksum mismatch for {sei.Name} {sei.Version}: expected {expectedSha}, got {actual}",
                        "checksum");
                }

                var temp = Path.Combine(Root, actual + ".tmp-" + Guid.NewGuid().ToString("N"));
                try
                {
                    extracted = Extract(artifactPath, temp);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    Directory.Move(temp, directory);
                    StoreDigests.Write(Root, new StoreDigestsState { Artifact = actual, Files = extracted });
                }
                catch
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, recursive: true);
                    }

                    throw;
                }
            }
            finally
            {
                session.ExtractSlots.Release();
            }

            TryDeleteDirectory(partialDirectory);
            session.ReportCompleted(sei.Name, sei.Artifact.Source.Size);
            return LoadExtracted(sei, directory, manifestPath, extracted);
        }
        catch (SeikoException ex) when (ex.Code == "checksum")
        {
            TryDeleteDirectory(partialDirectory);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
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
            // A later doctor/clean pass can remove leftovers.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private StoredSei LoadStored(ResolvedSei sei, string directory, string manifestPath)
    {
        var digests = LoadDigests(Hashing.NormalizeSha256(sei.Artifact.Source.Sha256), directory);
        VerifyFiles(directory, digests);
        var manifest = ReadManifest(directory, manifestPath);
        Validate(sei, manifest);
        return new StoredSei(directory, manifest, digests);
    }

    // The cold path just hashed every extracted file while writing it, so it
    // can reuse those digests; cache hits still run VerifyFiles.
    private static StoredSei LoadExtracted(
        ResolvedSei sei,
        string directory,
        string manifestPath,
        List<StoreFileDigest> extracted)
    {
        var manifest = ReadManifest(directory, manifestPath);
        Validate(sei, manifest);
        var digests = new Dictionary<string, StoreFileDigest>(StringComparer.Ordinal);
        foreach (var digest in extracted)
        {
            digests[digest.Path] = digest;
        }

        return new StoredSei(directory, manifest, digests);
    }

    private static SeiManifest ReadManifest(string directory, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize(
                File.ReadAllBytes(manifestPath),
                SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json in {directory}", "io");
        if (manifest.SchemaVersion != SeiManifest.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"unsupported sei.json schemaVersion {manifest.SchemaVersion} in {directory}",
                "io");
        }

        return manifest;
    }

    private static void VerifyFiles(
        string directory,
        IReadOnlyDictionary<string, StoreFileDigest> digests)
    {
        if (PathSafety.IsLink(directory))
        {
            throw CorruptedStore(directory, directory);
        }

        var recorded = new HashSet<string>(digests.Keys, StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (PathSafety.IsLink(entry))
                {
                    throw CorruptedStore(directory, entry);
                }

                var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (!recorded.Contains(relative))
                {
                    throw CorruptedStore(directory, entry);
                }
            }
        }

        foreach (var (relative, digest) in digests)
        {
            var path = ResolveStoreFile(directory, relative);
            var info = new FileInfo(path);
            if (!info.Exists
                || info.Length != digest.Size
                || !string.Equals(Hashing.Sha256File(path), digest.Sha256, StringComparison.Ordinal))
            {
                throw CorruptedStore(directory, path);
            }
        }
    }

    private static string ResolveStoreFile(string directory, string relative)
    {
        if (!StoreDigests.IsSafeRelativePath(relative))
        {
            throw CorruptedStore(directory, relative);
        }

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            throw CorruptedStore(directory, path);
        }

        return path;
    }

    private static SeikoException CorruptedStore(string directory, string path)
        => new(
            $"cache artifact is missing or corrupted ({path}); it will be downloaded again",
            "cache-repair");

    private IReadOnlyDictionary<string, StoreFileDigest> LoadDigests(string artifact, string directory)
    {
        var state = StoreDigests.TryRead(Root, artifact)
            ?? throw new SeikoException(
                $"cache metadata for {directory} is missing or unreadable",
                "cache-repair");

        var digests = new Dictionary<string, StoreFileDigest>(StringComparer.Ordinal);
        foreach (var file in state.Files ?? [])
        {
            digests[file.Path] = file;
        }

        return digests;
    }

    private static List<StoreFileDigest> Extract(string artifactPath, string directory)
    {
        Directory.CreateDirectory(directory);
        using var archive = ZipFile.OpenRead(artifactPath);
        ArchiveLimits.EnsureEntryCount(archive.Entries.Count);
        var digests = new List<StoreFileDigest>();
        var seenManifest = false;
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var declaredTotal = 0L;
        var writtenTotal = 0L;

        foreach (var entry in archive.Entries)
        {
            var name = ArchiveLimits.NormalizeName(entry.FullName);
            if (name.Length == 0)
            {
                continue;
            }

            if (ArchiveLimits.ExceedsEntryNameLimits(name))
            {
                throw new SeikoException(
                    $"zip entry path is too long or too deep: {name}",
                    "io");
            }

            if (name.Split('/').Any(segment => segment == ".."))
            {
                throw new SeikoException($"zip-slip entry rejected: {name}", "io");
            }

            if (!StoreDigests.IsSafeRelativePath(name))
            {
                throw new SeikoException($"unsafe zip entry path: {name}", "io");
            }

            if (!seenNames.Add(name))
            {
                throw new SeikoException($"duplicate zip entry: {name}", "io");
            }

            if (name == "sei.json")
            {
                if (seenManifest)
                {
                    throw new SeikoException("duplicate root sei.json in sei", "io");
                }

                seenManifest = true;
            }

            var destination = Path.GetFullPath(Path.Combine(directory, name));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
            {
                throw new SeikoException($"zip-slip entry rejected: {name}", "io");
            }

            ArchiveLimits.EnsureEntrySize(entry.Length);
            declaredTotal += entry.Length;
            ArchiveLimits.EnsureTotal(declaredTotal);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                size += read;
                ArchiveLimits.EnsureEntrySize(size);
                writtenTotal += read;
                ArchiveLimits.EnsureTotal(writtenTotal);
            }

            digests.Add(new StoreFileDigest
            {
                Path = name,
                Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                Size = size,
            });
        }

        if (!seenManifest)
        {
            throw new SeikoException("sei has no root sei.json", "io");
        }

        return digests;
    }

    private static void Validate(ResolvedSei sei, SeiManifest manifest)
    {
        if (manifest.SchemaVersion != SeiManifest.CurrentSchemaVersion)
        {
            throw new SeikoException($"{sei.Name}: unsupported sei.json schemaVersion {manifest.SchemaVersion}", "io");
        }

        if (!string.Equals(manifest.Name, sei.Name, StringComparison.Ordinal))
        {
            throw new SeikoException($"{sei.Name}: sei.json declares name '{manifest.Name}'", "io");
        }

        if (!NuGet.Versioning.NuGetVersion.TryParse(manifest.Version, out var manifestVersion)
            || !manifestVersion.Equals(sei.ParsedVersion))
        {
            throw new SeikoException($"{sei.Name}: sei.json declares version '{manifest.Version}', index has '{sei.Version}'", "io");
        }

        var dependencies = manifest.Dependencies;
        if (dependencies is null
            || dependencies.Any(dependency =>
                dependency is null || string.IsNullOrWhiteSpace(dependency.Name)))
        {
            throw new SeikoException(
                $"{sei.Name}: sei.json has an invalid dependency list",
                "io");
        }

        var manifestDeps = dependencies
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => $"{d.Name}|{d.Version}")
            .ToList();
        var indexDeps = sei.Entry.Dependencies
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => $"{d.Name}|{d.Version}")
            .ToList();
        if (!manifestDeps.SequenceEqual(indexDeps, StringComparer.Ordinal))
        {
            throw new SeikoException($"{sei.Name}: sei.json dependencies do not match the index", "io");
        }

        if (!RelationsEqual(manifest.Prerequisites, sei.Entry.Prerequisites)
            || !RelationsEqual(manifest.OptionalPeers, sei.Entry.OptionalPeers)
            || !RelationsEqual(manifest.Conflicts, sei.Entry.Conflicts))
        {
            throw new SeikoException($"{sei.Name}: sei.json relations do not match the index", "io");
        }

        if (manifest.Artifact is null)
        {
            throw new SeikoException($"{sei.Name}: sei.json has no artifact object", "io");
        }

        if (!string.Equals(manifest.Artifact.Rid, sei.Artifact.Rid, StringComparison.OrdinalIgnoreCase))
        {
            throw new SeikoException($"{sei.Name}: sei.json rid does not match the index artifact", "io");
        }

        if (!string.Equals(
            NormalizeLayout(manifest.Artifact.Layout),
            NormalizeLayout(sei.Artifact.Layout),
            StringComparison.Ordinal))
        {
            throw new SeikoException($"{sei.Name}: sei.json layout does not match the index artifact", "io");
        }

        if (!RelationsEqual(manifest.Artifact.Dependencies, sei.Artifact.Dependencies)
            || !RelationsEqual(manifest.Artifact.Prerequisites, sei.Artifact.Prerequisites)
            || !RelationsEqual(manifest.Artifact.OptionalPeers, sei.Artifact.OptionalPeers)
            || !RelationsEqual(manifest.Artifact.Conflicts, sei.Artifact.Conflicts))
        {
            throw new SeikoException($"{sei.Name}: sei.json artifact relations do not match the index artifact", "io");
        }

        if (!ModesEqual(manifest.Artifact.Modes, sei.Artifact.Modes))
        {
            throw new SeikoException($"{sei.Name}: sei.json modes do not match the index artifact", "io");
        }
    }

    private static string NormalizeLayout(string? layout)
        => string.IsNullOrEmpty(layout) ? "root" : layout;

    private static bool RelationsEqual(
        IReadOnlyList<SeiDependency>? left,
        IReadOnlyList<SeiDependency>? right)
    {
        var a = (left ?? []).OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        var b = (right ?? []).OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        return a.Count == b.Count
            && a.Zip(b).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && string.Equals(pair.First.Version ?? "", pair.Second.Version ?? "", StringComparison.Ordinal));
    }

    private static bool ModesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var a = left ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var b = right ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return a.Count == b.Count
            && a.All(pair =>
                b.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

}
