using System.IO.Compression;
using System.Text.Json;

namespace Seiko.Core;

public sealed record StoredPackage(string Directory, SeiManifest Manifest);

public sealed class Store
{
    public string Root { get; }

    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public StoredPackage? TryGet(string sha256)
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

        return new StoredPackage(directory, manifest);
    }

    public StoredPackage Ensure(ResolvedPackage package)
    {
        var expectedSha = package.Artifact.Source.Sha256.ToLowerInvariant();
        var directory = Path.Combine(Root, expectedSha);
        var manifestPath = Path.Combine(directory, "sei.json");
        if (File.Exists(manifestPath))
        {
            return LoadStored(package, directory, manifestPath);
        }

        var tempArtifact = Path.Combine(
            Path.GetTempPath(),
            "seiko-artifact-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            package.Source.DownloadArtifact(package.Artifact.Source, tempArtifact);
            var length = new FileInfo(tempArtifact).Length;
            if (package.Artifact.Source.Size > 0 && length != package.Artifact.Source.Size)
            {
                throw new SeikoException(
                    $"size mismatch for {package.Name} {package.Version}: expected {package.Artifact.Source.Size}, got {length}");
            }

            var actual = Hashing.Sha256File(tempArtifact);
            if (!actual.Equals(expectedSha, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"checksum mismatch for {package.Name} {package.Version}: expected {expectedSha}, got {actual}");
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

        return LoadStored(package, directory, manifestPath);
    }

    private static StoredPackage LoadStored(ResolvedPackage package, string directory, string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SeikoJsonContext.Default.SeiManifest)
            ?? throw new SeikoException($"invalid sei.json for {package.Name} {package.Version}");

        Validate(package, manifest);
        return new StoredPackage(directory, manifest);
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
                    throw new SeikoException("duplicate root sei.json in package");
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
            throw new SeikoException("package has no root sei.json");
        }
    }

    private static void Validate(ResolvedPackage package, SeiManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new SeikoException($"{package.Name}: unsupported sei.json schemaVersion {manifest.SchemaVersion}");
        }

        if (!string.Equals(manifest.Name, package.Name, StringComparison.Ordinal))
        {
            throw new SeikoException($"{package.Name}: sei.json declares name '{manifest.Name}'");
        }

        if (!NuGet.Versioning.NuGetVersion.TryParse(manifest.Version, out var manifestVersion)
            || !manifestVersion.Equals(package.ParsedVersion))
        {
            throw new SeikoException($"{package.Name}: sei.json declares version '{manifest.Version}', index has '{package.Version}'");
        }

        var manifestDeps = manifest.Dependencies
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => $"{d.Name}|{d.Version}")
            .ToList();
        var indexDeps = package.Entry.Dependencies
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => $"{d.Name}|{d.Version}")
            .ToList();
        if (!manifestDeps.SequenceEqual(indexDeps, StringComparer.Ordinal))
        {
            throw new SeikoException($"{package.Name}: sei.json dependencies do not match the index");
        }

        if (!SelectorEquals(manifest.Selectors, package.Artifact.Selectors))
        {
            throw new SeikoException($"{package.Name}: sei.json selectors do not match the index artifact");
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
