using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Seiko.Tests;

internal sealed record DepFixture(string Name, string? Version);

internal sealed record SpecFixture(string Name, string? Version = null);

internal sealed record ExternalFileFixture(string Path, string Url, string Content);

internal sealed record ExternalMemberFixture(string Member, string Path);

internal sealed record ExternalArchiveFixture(string Url, string Format, byte[] Content, params ExternalMemberFixture[] Members);

internal sealed class ArtifactFixture
{
    public string? Rid { get; init; }
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string>? Modes { get; init; }
    public List<DepFixture> Dependencies { get; init; } = [];
    public List<DepFixture> Prerequisites { get; init; } = [];
    public List<DepFixture> OptionalPeers { get; init; } = [];
    public List<DepFixture> Conflicts { get; init; } = [];
    public List<ExternalFileFixture> ExternalFiles { get; init; } = [];
    public List<ExternalArchiveFixture> ExternalArchives { get; init; } = [];
    public int ManifestSchemaVersion { get; init; } = SeiManifest.CurrentSchemaVersion;
    public string? OverriddenDigest { get; init; }
}

internal sealed class VersionFixture
{
    public required string Version { get; init; }
    public List<DepFixture> Dependencies { get; init; } = [];
    public List<DepFixture> Prerequisites { get; init; } = [];
    public List<DepFixture> OptionalPeers { get; init; } = [];
    public List<DepFixture> Conflicts { get; init; } = [];
    public List<ArtifactFixture> Artifacts { get; init; } = [];
    public bool Yanked { get; init; }
    public string? Deprecated { get; init; }
    public AdvisoryInfo? Advisory { get; init; }
}

internal sealed class Fixture : IDisposable
{
    private readonly List<string> _sei = [];
    private readonly Dictionary<string, SeiIndex> _indexes = new(StringComparer.Ordinal);
    private int _artifactIndex;

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

    // External bytes are written next to the artifacts so the test HTTP server
    // can expose them from the same root as the index.
    public string WriteExternal(string relative, byte[] content)
    {
        var path = Path.Combine(SourceDirectory, "external", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Tar(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(buffer, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile,
                    name)
                {
                    DataStream = new MemoryStream(content),
                };
                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Gzip(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
            buffer,
            System.IO.Compression.CompressionLevel.Optimal,
            leaveOpen: true))
        {
            gzip.Write(content);
        }

        return buffer.ToArray();
    }

    // Writes a standalone .sei artifact outside any index and returns its path
    // relative to the install root, the form `add` stores (ADR-0044).
    public string WriteOrigin(
        string name,
        string version,
        string file,
        string content,
        string? rid = null,
        (string Name, string? Version)[]? prerequisites = null,
        (string Name, string? Version)[]? dependencies = null,
        (string Name, string? Version)[]? manifestPrerequisites = null)
    {
        var directory = Path.Combine(Root, "dist");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".sei");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        var manifest = new SeiManifest
        {
            SchemaVersion = SeiManifest.CurrentSchemaVersion,
            Name = name,
            Version = version,
            Dependencies = ToDependencies(
                (dependencies ?? []).Select(item => new DepFixture(item.Name, item.Version))),
            Prerequisites = ToDependencies(
                (manifestPrerequisites ?? []).Select(item => new DepFixture(item.Name, item.Version))),
            Artifact = new ArtifactManifest
            {
                Layout = "root",
                Rid = rid,
                // The manifest never carries the selector condition; a caller
                // that needs one uses the selector path instead.
                Prerequisites = ToDependencies(
                    (prerequisites ?? []).Select(item => new DepFixture(item.Name, item.Version))),
            },
        };
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(zip, "sei.json", JsonSerializer.SerializeToUtf8Bytes(manifest, SeikoJsonContext.Default.SeiManifest));
            AddEntry(zip, file, Encoding.UTF8.GetBytes(content));
        }

        return "dist/" + name + ".sei";
    }

    public void AddSei(string name, params VersionFixture[] versions)
    {
        var index = _indexes.TryGetValue(name, out var existing)
            ? existing
            : new SeiIndex { Name = name };
        // Verifiers require project metadata; the fixture fills a minimal
        // record so tests that do not care about display fields stay terse.
        index.Project ??= new ProjectMetadata
        {
            Description = $"{name} test package",
            Authors = ["fixture"],
            License = "MIT",
        };        foreach (var version in versions)
        {
            var artifacts = new List<ArtifactEntry>();
            for (var i = 0; i < version.Artifacts.Count; i++)
            {
                var artifact = version.Artifacts[i];
                var zipName = $"{name}-{version.Version}-{_artifactIndex++}.zip";
                var zipPath = Path.Combine(SourceDirectory, "artifacts", zipName);
                var manifest = new SeiManifest
                {
                    SchemaVersion = artifact.ManifestSchemaVersion,
                    Name = name,
                    Version = version.Version,
                    Dependencies = ToDependencies(version.Dependencies),
                    Prerequisites = ToDependencies(version.Prerequisites),
                    OptionalPeers = ToDependencies(version.OptionalPeers),
                    Conflicts = ToDependencies(version.Conflicts),
                    Artifact = new ArtifactManifest
                    {
                        Rid = artifact.Rid,
                        Dependencies = ToDependencies(artifact.Dependencies),
                        Prerequisites = ToDependencies(artifact.Prerequisites),
                        OptionalPeers = ToDependencies(artifact.OptionalPeers),
                        Conflicts = ToDependencies(artifact.Conflicts),
                        Layout = "root",
                        Modes = artifact.Modes,
                        ExternalFiles = artifact.ExternalFiles
                            .Select(file => new ExternalFile
                            {
                                Path = file.Path,
                                Url = file.Url,
                                Sha256 = Hashing.Sha256Bytes(Encoding.UTF8.GetBytes(file.Content)),
                                Size = Encoding.UTF8.GetByteCount(file.Content),
                            })
                            .ToList(),
                        ExternalArchives = artifact.ExternalArchives
                            .Select(archive => new ExternalArchive
                            {
                                Url = archive.Url,
                                Format = archive.Format,
                                Sha256 = Hashing.Sha256Bytes(archive.Content),
                                Size = archive.Content.Length,
                                Members = archive.Members
                                    .Select(member => new ExternalArchiveMember
                                    {
                                        Member = member.Member,
                                        Path = member.Path,
                                    })
                                    .ToList(),
                            })
                            .ToList(),
                    },
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
                    Rid = artifact.Rid,
                    Dependencies = ToDependencies(artifact.Dependencies),
                    Prerequisites = ToDependencies(artifact.Prerequisites),
                    OptionalPeers = ToDependencies(artifact.OptionalPeers),
                    Conflicts = ToDependencies(artifact.Conflicts),
                    Layout = "root",
                    Modes = artifact.Modes,
                    ExternalFiles = artifact.ExternalFiles
                        .Select(file => new ExternalFile
                        {
                            Path = file.Path,
                            Url = file.Url,
                            Sha256 = Hashing.Sha256Bytes(Encoding.UTF8.GetBytes(file.Content)),
                            Size = Encoding.UTF8.GetByteCount(file.Content),
                        })
                        .ToList(),
                    ExternalArchives = artifact.ExternalArchives
                        .Select(archive => new ExternalArchive
                        {
                            Url = archive.Url,
                            Format = archive.Format,
                            Sha256 = Hashing.Sha256Bytes(archive.Content),
                            Size = archive.Content.Length,
                            Members = archive.Members
                                .Select(member => new ExternalArchiveMember
                                {
                                    Member = member.Member,
                                    Path = member.Path,
                                })
                                .ToList(),
                        })
                        .ToList(),
                    External = artifact.ExternalFiles.Count + artifact.ExternalArchives.Count == 0
                        ? null
                        : new ExternalSummary
                        {
                            Files = artifact.ExternalFiles.Count,
                            Archives = artifact.ExternalArchives.Count,
                            Size = artifact.ExternalFiles.Sum(file => Encoding.UTF8.GetByteCount(file.Content))
                                + artifact.ExternalArchives.Sum(archive => (long)archive.Content.Length),
                            Hosts = artifact.ExternalFiles.Select(file => new Uri(file.Url).Host)
                                .Concat(artifact.ExternalArchives.Select(archive => new Uri(archive.Url).Host))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                        },
                    Source = new ArtifactLocation
                    {
                        Url = "artifacts/" + zipName,
                        Sha256 = artifact.OverriddenDigest ?? Hashing.Sha256File(zipPath),
                        Size = info.Length,
                    },
                });
            }

            index.Versions.Add(new SeiVersionEntry
            {
                Version = version.Version,
                Dependencies = ToDependencies(version.Dependencies),
                Prerequisites = ToDependencies(version.Prerequisites),
                OptionalPeers = ToDependencies(version.OptionalPeers),
                Conflicts = ToDependencies(version.Conflicts),
                Artifacts = artifacts,
                Yanked = version.Yanked,
                Deprecated = version.Deprecated,
                Advisory = version.Advisory,
            });
        }

        _indexes[name] = index;
        if (!_sei.Contains(name, StringComparer.Ordinal))
        {
            _sei.Add(name);
        }

        WriteSei(name);
    }

    public void MarkVersionYanked(string name, string version)
    {
        var entry = _indexes[name].Versions.Single(item => item.Version == version);
        entry.Yanked = true;
        WriteSei(name);
    }

    public void MarkPackage(string name, bool unlisted, string? deprecated = null)
    {
        var index = _indexes[name];
        index.Unlisted = unlisted;
        if (deprecated is not null)
        {
            index.Deprecated = deprecated;
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
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}",
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

    /// <summary>
    /// Tests that edit a sei file directly must regenerate the catalog, as the
    /// index bot does for every change. Regenerates from disk, not from the
    /// in-memory copy, so direct file edits are picked up.
    /// </summary>
    public void RefreshCatalog()
    {
        var indexes = Directory
            .EnumerateFiles(Path.Combine(SourceDirectory, "sei"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<SeiIndex>(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.SeiIndex)!)
            .Where(index => index is not null);
        File.WriteAllBytes(
            Path.Combine(SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                CatalogFile.Build(indexes),
                SeikoJsonContext.Default.SeiCatalog));
    }

    private void WriteSei(string name)
    {
        var path = Path.Combine(SourceDirectory, "sei", name + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(_indexes[name], SeikoJsonContext.Default.SeiIndex));

        // The real index bot regenerates the catalog on every change; mirror
        // that so verifier tests do not see a stale catalog by default.
        File.WriteAllBytes(
            Path.Combine(SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                CatalogFile.Build(_indexes.Values),
                SeikoJsonContext.Default.SeiCatalog));
    }

    private static List<SeiDependency> ToDependencies(IEnumerable<DepFixture> dependencies)
        => dependencies
            .Select(dependency => new SeiDependency
            {
                Name = dependency.Name,
                Version = dependency.Version,
            })
            .ToList();

    private static void AddEntry(ZipArchive zip, string name, byte[] contents)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(contents);
    }
}
