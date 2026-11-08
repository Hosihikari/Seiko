using System.Text.Json;

namespace Seiko;

// A direct declaration backed by a local .sei file, presented to the resolver
// as the first source so it wins over anything configured (ADR-0044). It
// answers exactly one name with exactly one version and one artifact: a
// degenerate index, not a new source kind.
internal sealed class OriginSource : ISeiSource
{
    private readonly OriginSpec _spec;
    private readonly string _rid;
    private string? _preferred;

    public OriginSource(OriginSpec spec, string root, string rid, string? storeRoot = null)
    {
        _spec = spec;
        Root = root;
        _rid = rid;
        StoreRoot = storeRoot;
    }

    public string Id => OriginPaths.SourceId;

    public string Root { get; }

    public string? StoreRoot { get; }

    public SeiCatalog? Catalog => null;

    public IndexRoot Index => new() { Sei = [_spec.Name] };

    public SeiIndex? GetSei(string name)
        => string.Equals(name, _spec.Name, StringComparison.Ordinal) ? Build() : null;

    // Used by `add` before the declaration exists; reads the name only.
    public static string ReadName(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.Entries
            .FirstOrDefault(item => ArchiveLimits.NormalizeName(item.FullName) == "sei.json")
            ?? throw new SeikoException(
                $"{Path.GetFileName(path)}: artifact has no root sei.json",
                "io");
        SeiManifest? manifest;
        try
        {
            using var stream = entry.Open();
            manifest = JsonSerializer.Deserialize(stream, SeikoJsonContext.Default.SeiManifest);
        }
        catch (JsonException ex)
        {
            throw new SeikoException($"{Path.GetFileName(path)}: invalid sei.json ({ex.Message})", "io");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new SeikoException($"{Path.GetFileName(path)}: sei.json has no name", "io");
        }

        if (!SeiName.IsValid(manifest.Name))
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)}: invalid sei name '{manifest.Name}'",
                "usage");
        }

        return manifest.Name;
    }

    public string ResolveArtifact(ArtifactLocation source) => source.Url;

    private string? SnapshotFor(string name)
    {
        if (StoreRoot is null)
        {
            return null;
        }

        if (_preferred is not null)
        {
            var exact = OriginSnapshot.SnapshotPath(StoreRoot, name, _preferred);
            return File.Exists(exact) ? exact : null;
        }

        var directory = OriginSnapshot.Folder(StoreRoot);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, name + "-*.sei")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // The stored form for the lock: relative to the install root.

    // Used by `--frozen` and `--locked`: the lock already names the bytes, so
    // the snapshot is chosen by that digest rather than by recency.
    public void PreferDigest(string sha256) => _preferred = Hashing.NormalizeSha256(sha256);
    public string? PathOf(ArtifactEntry artifact)
    {
        var full = artifact.Source.Url;
        foreach (var stored in _spec.Paths)
        {
            if (string.Equals(OriginPaths.Resolve(stored, Root), full, StringComparison.Ordinal))
            {
                return stored;
            }
        }

        return null;
    }

    public Task DownloadAsync(
        string name,
        ArtifactLocation source,
        string directory,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, "artifact");
        // The original file is the first choice; the store snapshot only
        // covers a deleted or moved original. The caller verifies the digest
        // either way, so a rewritten file that no longer matches fails there.
        var sourcePath = File.Exists(source.Url)
            ? source.Url
            : OriginSnapshot.SnapshotPath(StoreRoot ?? Root, name, source.Sha256);
        if (!File.Exists(sourcePath))
        {
            throw new SeikoException(
                $"{name}: neither the origin file nor the store snapshot for {source.Sha256} exists",
                "checksum");
        }

        session.Log?.Invoke($"{name}: copying {sourcePath}");
        session.ReportDownloading(name, 0, source.Size);
        File.Copy(sourcePath, destination, overwrite: true);
        session.ReportDownloading(name, source.Size, source.Size);
        return Task.CompletedTask;
    }

    private SeiIndex Build()
    {
        var chosen = Choose();
        var path = OriginPaths.Resolve(chosen.Path, Root);
        // Resolution reads the manifest too, so a missing original has to fall
        // back before the download stage. The digest is not known yet; any
        // snapshot of this name that is picked still has to survive the
        // caller's digest check, and a stale one fails there.
        if (!File.Exists(path) && StoreRoot is not null)
        {
            path = SnapshotFor(_spec.Name) ?? path;
        }

        if (!File.Exists(path))
        {
            throw new SeikoException(
                $"{_spec.Name}: the origin file is gone and no snapshot matches"
                + (_preferred is null ? "; add it again" : $"; {_preferred} has no snapshot"),
                "checksum");
        }

        var manifest = ReadManifest(path, _spec.Name);
        if (ExternalFiles.Count(manifest.Artifact) > 0)
        {
            throw new SeikoException(
                $"{manifest.Name}: a local file cannot serve external files",
                "source");
        }

        var artifact = manifest.Artifact!;
        var location = new ArtifactLocation
        {
            Url = path,
            Sha256 = Hashing.Sha256File(path),
            Size = new FileInfo(path).Length,
        };
        return new SeiIndex
        {
            Name = manifest.Name,
            Versions =
            [
                new SeiVersionEntry
                {
                    Version = manifest.Version,
                    Dependencies = manifest.Dependencies,
                    Prerequisites = manifest.Prerequisites,
                    OptionalPeers = manifest.OptionalPeers,
                    Conflicts = manifest.Conflicts,
                    Artifacts =
                    [
                        new ArtifactEntry
                        {
                            Rid = chosen.Selector?.Rid,
                            Prerequisites = artifact.Prerequisites,
                            Dependencies = artifact.Dependencies,
                            OptionalPeers = artifact.OptionalPeers,
                            Conflicts = artifact.Conflicts,
                            Layout = artifact.Layout,
                            Modes = artifact.Modes,
                            Source = location,
                        },
                    ],
                },
            ],
        };
    }

    // Declarations are parallel to their selectors; the first one whose
    // condition holds wins. Two matching a condition with nothing to tell them
    // apart is an ambiguity the user has to resolve, matching how the resolver
    // treats equal artifact conditions.
    private (string Path, OriginSelector? Selector) Choose()
    {
        var matches = new List<(string Path, OriginSelector? Selector)>();
        for (var i = 0; i < _spec.Paths.Count; i++)
        {
            var selector = _spec.Selectors[i];
            if (string.IsNullOrWhiteSpace(selector.Rid)
                || string.Equals(selector.Rid, "*", StringComparison.Ordinal)
                || string.Equals(selector.Rid, _rid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((_spec.Paths[i], selector));
            }
        }

        if (matches.Count == 0)
        {
            throw new SeikoException(
                $"{_spec.Name}: no origin file matches rid '{_rid}'",
                "resolve");
        }

        var first = matches[0].Selector;
        var ambiguous = matches
            .Skip(1)
            .FirstOrDefault(match => string.Equals(
                ArtifactConditions.Key(first?.Rid, first?.Prerequisites),
                ArtifactConditions.Key(match.Selector?.Rid, match.Selector?.Prerequisites),
                StringComparison.Ordinal));
        if (ambiguous.Path is not null)
        {
            throw new SeikoException(
                $"{_spec.Name}: origin paths '{matches[0].Path}' and '{ambiguous.Path}' have the same condition",
                "resolve");
        }

        return matches[0];
    }

    private SeiManifest ReadManifest(string path, string name)
    {
        if (new FileInfo(path).Length > ArchiveLimits.MaxManifestBytes)
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)} exceeds the {ArchiveLimits.MaxManifestBytes}-byte manifest limit",
                "checksum");
        }

        System.IO.Compression.ZipArchive archive;
        try
        {
            archive = System.IO.Compression.ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new SeikoException($"{Path.GetFileName(path)} is not a zip archive", "io");
        }

        using (archive)
        {
            var manifests = archive.Entries
                .Where(entry => ArchiveLimits.NormalizeName(entry.FullName) == "sei.json")
                .ToList();
            if (manifests.Count != 1)
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)}: artifact must have exactly one root sei.json",
                    "io");
            }

            SeiManifest? manifest;
            try
            {
                using var stream = manifests[0].Open();
                manifest = JsonSerializer.Deserialize(stream, SeikoJsonContext.Default.SeiManifest);
            }
            catch (JsonException ex)
            {
                throw new SeikoException($"{Path.GetFileName(path)}: invalid sei.json ({ex.Message})", "io");
            }

            if (manifest is null)
            {
                throw new SeikoException($"{Path.GetFileName(path)}: sei.json is empty", "io");
            }

            // 1 predates the artifact object; there is no migration layer for
            // local files (ADR-0044, ADR-0031).
            if (manifest.SchemaVersion is not (2 or SeiManifest.CurrentSchemaVersion))
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)}: unsupported sei.json schemaVersion {manifest.SchemaVersion}",
                    "io");
            }

            if (manifest.Artifact is null)
            {
                throw new SeikoException($"{Path.GetFileName(path)}: sei.json has no artifact object", "io");
            }

            if (!string.Equals(manifest.Name, name, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)}: sei.json declares name '{manifest.Name}', expected '{name}'",
                    "usage");
            }

            if (!OriginPaths.StemMatches(path, manifest.Name))
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)}: file name must start with '{manifest.Name}'",
                    "usage");
            }

            return manifest;
        }
    }
}
