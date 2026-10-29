using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Seiko.Tests;

internal sealed record DepFixture(string Name, string? Version);

internal sealed record SpecFixture(string Name, string? Version = null);

internal sealed class ArtifactFixture
{
    public Dictionary<string, string>? Selectors { get; init; }
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string>? Modes { get; init; }
}

internal sealed class VersionFixture
{
    public required string Version { get; init; }
    public List<DepFixture> Dependencies { get; init; } = [];
    public List<ArtifactFixture> Artifacts { get; init; } = [];
    public bool Yanked { get; init; }
}

internal sealed class Fixture : IDisposable
{
    private readonly List<string> _sei = [];
    private readonly Dictionary<string, SeiIndex> _indexes = new(StringComparer.Ordinal);

    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        SourceDirectory = Path.Combine(Root, "source");
        StoreDirectory = Path.Combine(Root, "store");
        Directory.CreateDirectory(Path.Combine(SourceDirectory, "sei"));
        Directory.CreateDirectory(Path.Combine(SourceDirectory, "artifacts"));
        Directory.CreateDirectory(StoreDirectory);
    }

    public string Root { get; }
    public string SourceDirectory { get; }
    public string StoreDirectory { get; }

    public void AddSei(string name, params VersionFixture[] versions)
    {
        var index = _indexes.TryGetValue(name, out var existing)
            ? existing
            : new SeiIndex { Name = name };
        foreach (var version in versions)
        {
            var artifacts = new List<ArtifactEntry>();
            for (var i = 0; i < version.Artifacts.Count; i++)
            {
                var artifact = version.Artifacts[i];
                var zipName = $"{name}-{version.Version}-{i}.zip";
                var zipPath = Path.Combine(SourceDirectory, "artifacts", zipName);
                var manifest = new SeiManifest
                {
                    Name = name,
                    Version = version.Version,
                    Dependencies = version.Dependencies
                        .Select(d => new SeiDependency { Name = d.Name, Version = d.Version })
                        .ToList(),
                    Selectors = artifact.Selectors,
                    Layout = "root",
                    Modes = artifact.Modes,
                };

                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    AddEntry(zip, "sei.json", JsonSerializer.SerializeToUtf8Bytes(manifest, SeikoJsonContext.Default.SeiManifest));
                    foreach (var (file, content) in artifact.Files)
                    {
                        AddEntry(zip, file, Encoding.UTF8.GetBytes(content));
                    }
                }

                var info = new FileInfo(zipPath);
                artifacts.Add(new ArtifactEntry
                {
                    Selectors = artifact.Selectors,
                    Layout = "root",
                    Source = new ArtifactLocation
                    {
                        Url = "artifacts/" + zipName,
                        Sha256 = Hashing.Sha256File(zipPath),
                        Size = info.Length,
                    },
                });
            }

            index.Versions.Add(new SeiVersionEntry
            {
                Version = version.Version,
                Dependencies = version.Dependencies
                    .Select(d => new SeiDependency { Name = d.Name, Version = d.Version })
                    .ToList(),
                Artifacts = artifacts,
                Yanked = version.Yanked,
            });
        }

        _indexes[name] = index;
        if (!_sei.Contains(name, StringComparer.Ordinal))
        {
            _sei.Add(name);
        }

        WriteSei(name);
    }

    public void WriteConfig(string rid, params SpecFixture[] specs)
    {
        var indexPath = Path.Combine(SourceDirectory, "index.json").Replace('\\', '/');
        WriteConfigCore("local", indexPath, rid, specs);
    }

    public void WriteHttpConfig(string baseUrl, string rid, params SpecFixture[] specs)
    {
        WriteConfigCore("http", baseUrl.TrimEnd('/') + "/index.json", rid, specs);
    }

    private void WriteConfigCore(string sourceId, string indexUrl, string rid, SpecFixture[] specs)
    {
        var index = new IndexRoot
        {
            GeneratedBy = "fixture",
            Sei = _sei.OrderBy(name => name, StringComparer.Ordinal).ToList(),
        };
        File.WriteAllBytes(
            Path.Combine(SourceDirectory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.IndexRoot));

        var lines = new List<string>
        {
            "sources = [",
            $"  {{ id = \"{sourceId}\", index = \"{indexUrl}\" }}",
            "]",
            $"rid = \"{rid}\"",
        };

        if (specs.Length == 0)
        {
            lines.Add("sei = []");
        }
        else
        {
            lines.Add("sei = [");
            for (var i = 0; i < specs.Length; i++)
            {
                var spec = specs[i];
                var version = spec.Version is null ? "" : $", version = \"{spec.Version}\"";
                var comma = i == specs.Length - 1 ? "" : ",";
                lines.Add($"  {{ name = \"{spec.Name}\"{version} }}{comma}");
            }

            lines.Add("]");
        }

        lines.Add("components = []");
        File.WriteAllText(Path.Combine(Root, "seiko.conf"), string.Join("\n", lines) + "\n");
    }

    public void CorruptChecksum(string name)
    {
        var index = _indexes[name];
        index.Versions[0].Artifacts[0].Source.Sha256 = new string('0', 64);
        WriteSei(name);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private void WriteSei(string name)
    {
        var path = Path.Combine(SourceDirectory, "sei", name + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(_indexes[name], SeikoJsonContext.Default.SeiIndex));
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] contents)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(contents);
    }
}
