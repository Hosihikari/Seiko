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
    // Visible to tests: how many EnsureAsync calls are inside one store at
    // once. Scheduled downloads must stay bounded by --jobs. Keyed by root so
    // tests running in parallel do not disturb each other.
    private sealed class EnsureStats
    {
        public int Active;
        public int Max;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EnsureStats> Stats =
        new(StringComparer.Ordinal);

    internal static int MaxActiveEnsures(string root)
        => Stats.TryGetValue(root, out var stats) ? Volatile.Read(ref stats.Max) : 0;

    internal static void ResetEnsureStats(string root)
        => Stats[root] = new EnsureStats();

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

    // Same as EnsureAsync plus the concurrency statistic the scheduling bound
    // is tested with.
    internal async Task<StoredSei> EnsureCountedAsync(
        ResolvedSei sei,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var stats = Stats.GetOrAdd(Root, _ => new EnsureStats());
        var active = Interlocked.Increment(ref stats.Active);
        int max;
        while (active > (max = Volatile.Read(ref stats.Max))
            && Interlocked.CompareExchange(ref stats.Max, active, max) != max)
        {
        }

        try
        {
            return await EnsureAsync(sei, session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref stats.Active);
        }
    }

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
                    var manifest = ReadManifest(temp, Path.Combine(temp, "sei.json"));
                    // The warm path validates in LoadStored/LoadExtracted; the
                    // cold path must do it before the steps below dereference
                    // the artifact, or a manifest without one fails differently
                    // on the first install than on the second (ADR-0024).
                    Validate(sei, manifest);
                    ExternalFiles.Validate(manifest.Artifact!, sei.Name);
                    EnsureNoTargetCollision(extracted, manifest, sei.Name);
                    ExternalMaterializer.Run(
                        sei,
                        manifest,
                        temp,
                        Root,
                        actual,
                        extracted,
                        session,
                        cancellationToken);
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

    // The cold path computed digests while extracting, but ADR-0024 requires
    // the same full verify as a cache hit: the bytes on disk must be hashed
    // again rather than trusting the extraction pass.
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

        VerifyFiles(directory, digests);
        return new StoredSei(directory, manifest, digests);
    }

    private static SeiManifest ReadManifest(string directory, string manifestPath)
    {
        if (new FileInfo(manifestPath).Length > ArchiveLimits.MaxManifestBytes)
        {
            throw new SeikoException(
                $"sei.json in {directory} exceeds the {ArchiveLimits.MaxManifestBytes}-byte limit",
                "checksum");
        }

        SeiManifest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(
                File.ReadAllBytes(manifestPath),
                SeikoJsonContext.Default.SeiManifest);
        }
        catch (JsonException ex)
        {
            // A remote manifest is data, so a bad field name is an artifact
            // problem, not an internal failure.
            throw new SeikoException($"invalid sei.json in {directory} ({ex.Message})", "io", ex);
        }

        var manifest = parsed
            ?? throw new SeikoException($"invalid sei.json in {directory}", "io");
        if (manifest.SchemaVersion is < 2 or > SeiManifest.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"unsupported sei.json schemaVersion {manifest.SchemaVersion} in {directory}",
                "io");
        }

        // Only a 3 manifest may carry external declarations; 2 is the
        // pre-external format and must stay free of them.
        if (manifest.SchemaVersion < 3 && ExternalFiles.Count(manifest.Artifact) > 0)
        {
            throw new SeikoException(
                $"{directory}: sei.json schemaVersion {manifest.SchemaVersion} cannot declare external files",
                "io");
        }

        // `preserve` and `dependencyGroups` are 4 fields. A lower manifest
        // that carries them is a version declaration error, not a package to
        // install with the fields guessed at.
        if (manifest.SchemaVersion < 4 && manifest.Artifact?.Preserve is not null)
        {
            throw new SeikoException(
                $"{directory}: sei.json schemaVersion {manifest.SchemaVersion} cannot declare preserve",
                "io");
        }

        if (manifest.SchemaVersion < 4 && manifest.Artifact?.DependencyGroups is not null)
        {
            throw new SeikoException(
                $"{directory}: sei.json schemaVersion {manifest.SchemaVersion} cannot declare dependencyGroups",
                "io");
        }

        return manifest;
    }

    // An external target may not collide with a file that came out of the
    // metadata zip; the two sources would silently disagree. The rule matches
    // deployment: exact, case, Unicode normalization, and file/directory
    // prefix overlaps (ADR-0024).
    private static void EnsureNoTargetCollision(
        List<StoreFileDigest> extracted,
        SeiManifest manifest,
        string name)
    {
        var artifact = manifest.Artifact;
        if (artifact is null)
        {
            return;
        }

        var collisions = new PathCollisions();
        foreach (var digest in extracted)
        {
            collisions.Add(digest.Path);
        }

        foreach (var target in (artifact.ExternalFiles ?? []).Select(file => file.Path)
            .Concat((artifact.ExternalArchives ?? []).SelectMany(archive => archive.Members).Select(member => member.Path)))
        {
            collisions.Add(target);
        }

        // One pass over the whole tree tells which paths collide with which;
        // declarations were already checked against each other on their own.
        if (collisions.TryFind(out var collision))
        {
            throw new SeikoException(
                collision!.Kind == PathCollisionKind.SamePath
                    ? $"{name}: path '{collision.Other}' also exists inside the artifact by case or Unicode normalization"
                    : $"{name}: path '{collision.Other}' is both a file and a directory of another deployed path",
                "io");
        }
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
        if (manifest.SchemaVersion is < 2 or > SeiManifest.CurrentSchemaVersion)
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

        if (NormalizeLayout(manifest.Artifact.Layout) != "root")
        {
            throw new SeikoException(
                $"{sei.Name}: sei.json has unsupported layout '{manifest.Artifact.Layout}'",
                "io");
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

        // An origin artifact carries its own declaration and its selector
        // condition, and there is no index entry to compare against, so its
        // prerequisite list legitimately differs from the manifest's (ADR-0044).
        if (!RelationsEqual(manifest.Artifact.Dependencies, sei.Artifact.Dependencies)
            || (sei.Source is not OriginSource
                && !RelationsEqual(manifest.Artifact.Prerequisites, sei.Artifact.Prerequisites))
            || !RelationsEqual(manifest.Artifact.OptionalPeers, sei.Artifact.OptionalPeers)
            || !RelationsEqual(manifest.Artifact.Conflicts, sei.Artifact.Conflicts))
        {
            throw new SeikoException($"{sei.Name}: sei.json artifact relations do not match the index artifact", "io");
        }

        if (!ModesEqual(manifest.Artifact.Modes, sei.Artifact.Modes))
        {
            throw new SeikoException($"{sei.Name}: sei.json modes do not match the index artifact", "io");
        }

        // The index entry selects the group, so the manifest's whole list must
        // agree; a mismatch would resolve against relations installation never
        // sees (ADR-0045). preserve follows the same rule: the index entry is
        // what previews read, so a disagreement would preview the wrong
        // ownership.
        if (!DependencyGroups.Equal(manifest.Artifact.DependencyGroups, sei.Artifact.DependencyGroups))
        {
            throw new SeikoException($"{sei.Name}: sei.json dependency groups do not match the index artifact", "io");
        }

        if (!DependencyGroups.RelationsEqual(
            (manifest.Artifact.Preserve ?? []).Select(path => new SeiDependency { Name = path }).ToList(),
            (sei.Artifact.Preserve ?? []).Select(path => new SeiDependency { Name = path }).ToList()))
        {
            throw new SeikoException($"{sei.Name}: sei.json preserve does not match the index artifact", "io");
        }
    }

    private static string NormalizeLayout(string? layout)
        => string.IsNullOrEmpty(layout) ? "root" : layout;

    private static bool RelationsEqual(
        IReadOnlyList<SeiDependency>? left,
        IReadOnlyList<SeiDependency>? right)
    {
        var a = (left ?? [])
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Version ?? "", StringComparer.Ordinal)
            .ToList();
        var b = (right ?? [])
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Version ?? "", StringComparer.Ordinal)
            .ToList();
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
