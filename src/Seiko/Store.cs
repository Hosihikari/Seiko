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
        var directory = Path.Combine(Root, sha256);
        var manifestPath = Path.Combine(directory, "sei.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var digests = LoadDigests(sha256, directory);
        VerifyFiles(directory, digests);
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json in {directory}", "io");
        if (manifest.SchemaVersion != 1)
        {
            throw new SeikoException($"unsupported sei.json schemaVersion {manifest.SchemaVersion} in {directory}", "io");
        }

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
        if (File.Exists(manifestPath))
        {
            session.ReportCompleted(sei.Name, sei.Artifact.Source.Size);
            return LoadStored(sei, directory, manifestPath);
        }

        var partialRoot = Path.Combine(Root, ".partial");
        Directory.CreateDirectory(partialRoot);
        var partialDirectory = Path.Combine(partialRoot, expectedSha);
        using var artifactLock = ProcessLock.Acquire(
            Path.Combine(Root, ".locks", expectedSha + ".lock"),
            $"artifact {expectedSha}");

        if (File.Exists(manifestPath))
        {
            return LoadStored(sei, directory, manifestPath);
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
                    var digests = Extract(artifactPath, temp);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    Directory.Move(temp, directory);
                    StoreDigests.Write(Root, new StoreDigestsState { Artifact = actual, Files = digests });
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
            return LoadStored(sei, directory, manifestPath);
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
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json for {sei.Name} {sei.Version}", "io");
        Validate(sei, manifest);
        return new StoredSei(directory, manifest, digests);
    }

    private static void VerifyFiles(
        string directory,
        IReadOnlyDictionary<string, StoreFileDigest> digests)
    {
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
        if (string.IsNullOrEmpty(relative)
            || Path.IsPathRooted(relative)
            || relative.Split('/').Any(segment => segment is "" or ".."))
        {
            throw CorruptedStore(directory, Path.Combine(directory, relative));
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
            $"store artifact is corrupted ({path}); delete {directory} and run `sk sync` to download it again",
            "checksum");

    private IReadOnlyDictionary<string, StoreFileDigest> LoadDigests(string artifact, string directory)
    {
        var state = StoreDigests.TryRead(Root, artifact);
        if (state is null)
        {
            // Artifacts extracted by older versions have no digest record yet;
            // adopt their current contents as the baseline.
            state = StoreDigests.Compute(directory, artifact);
            try
            {
                StoreDigests.Write(Root, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

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
        var digests = new List<StoreFileDigest>();
        var seenManifest = false;
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.Split('/').Any(segment => segment == ".."))
            {
                throw new SeikoException($"zip-slip entry rejected: {name}", "io");
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
        if (manifest.SchemaVersion != 1)
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

        var manifestDeps = manifest.Dependencies
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

        if (!SelectorEquals(manifest.Selectors, sei.Artifact.Selectors))
        {
            throw new SeikoException($"{sei.Name}: sei.json selectors do not match the index artifact", "io");
        }
    }

    private static bool SelectorEquals(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
