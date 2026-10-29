using System.IO.Compression;
using System.Text.Json;

namespace Seiko;

public sealed record StoredSei(string Directory, SeiManifest Manifest);

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
        var directory = Path.Combine(Root, sha256);
        var manifestPath = Path.Combine(directory, "sei.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json in {directory}");

        if (manifest.SchemaVersion != 1)
        {
            throw new SeikoException($"unsupported sei.json schemaVersion {manifest.SchemaVersion} in {directory}");
        }

        return new StoredSei(directory, manifest);
    }

    public StoredSei Ensure(ResolvedSei sei)
    {
        var expectedSha = sei.Artifact.Source.Sha256.ToLowerInvariant();
        var directory = Path.Combine(Root, expectedSha);
        var manifestPath = Path.Combine(directory, "sei.json");
        if (File.Exists(manifestPath))
        {
            return LoadStored(sei, directory, manifestPath);
        }

        var tempArtifact = Path.Combine(
            Path.GetTempPath(),
            "seiko-artifact-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            sei.Source.DownloadArtifact(sei.Artifact.Source, tempArtifact);
            var length = new FileInfo(tempArtifact).Length;
            if (sei.Artifact.Source.Size > 0 && length != sei.Artifact.Source.Size)
            {
                throw new SeikoException(
                    $"size mismatch for {sei.Name} {sei.Version}: expected {sei.Artifact.Source.Size}, got {length}");
            }

            var actual = Hashing.Sha256File(tempArtifact);
            if (!actual.Equals(expectedSha, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"checksum mismatch for {sei.Name} {sei.Version}: expected {expectedSha}, got {actual}");
            }

            var temp = Path.Combine(Root, actual + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                Extract(tempArtifact, temp);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                Directory.Move(temp, directory);
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
            if (File.Exists(tempArtifact))
            {
                File.Delete(tempArtifact);
            }
        }

        return LoadStored(sei, directory, manifestPath);
    }

    private static StoredSei LoadStored(ResolvedSei sei, string directory, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json for {sei.Name} {sei.Version}");

        Validate(sei, manifest);
        return new StoredSei(directory, manifest);
    }

    private static void Extract(string artifactPath, string directory)
    {
        Directory.CreateDirectory(directory);
        using var archive = ZipFile.OpenRead(artifactPath);
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
                throw new SeikoException($"zip-slip entry rejected: {name}");
            }

            if (name == "sei.json")
            {
                if (seenManifest)
                {
                    throw new SeikoException("duplicate root sei.json in sei");
                }

                seenManifest = true;
            }

            var destination = Path.GetFullPath(Path.Combine(directory, name));
            if (!destination.StartsWith(root, StringComparison.Ordinal))
            {
                throw new SeikoException($"zip-slip entry rejected: {name}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }

        if (!seenManifest)
        {
            throw new SeikoException("sei has no root sei.json");
        }
    }

    private static void Validate(ResolvedSei sei, SeiManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new SeikoException($"{sei.Name}: unsupported sei.json schemaVersion {manifest.SchemaVersion}");
        }

        if (!string.Equals(manifest.Name, sei.Name, StringComparison.Ordinal))
        {
            throw new SeikoException($"{sei.Name}: sei.json declares name '{manifest.Name}'");
        }

        if (!NuGet.Versioning.NuGetVersion.TryParse(manifest.Version, out var manifestVersion)
            || !manifestVersion.Equals(sei.ParsedVersion))
        {
            throw new SeikoException($"{sei.Name}: sei.json declares version '{manifest.Version}', index has '{sei.Version}'");
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
            throw new SeikoException($"{sei.Name}: sei.json dependencies do not match the index");
        }

        if (!SelectorEquals(manifest.Selectors, sei.Artifact.Selectors))
        {
            throw new SeikoException($"{sei.Name}: sei.json selectors do not match the index artifact");
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
