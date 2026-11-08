using System.Text.Json;

namespace Seiko;

// Every origin declaration, presented to the resolver as one source that
// answers before anything configured (ADR-0044). Each name is a degenerate
// index: one version, one artifact per matching selector. All entries share
// the reserved `origin` id, so the lock tells them apart by `originPath`.
internal sealed class OriginSource : ISeiSource
{
    private readonly Dictionary<string, OriginSpec> _specs;
    private readonly Dictionary<string, string> _preferred = new(StringComparer.Ordinal);
    // The locked replay record, keyed by name: path, digest and the artifact
    // conditions the lock pinned. A replay builds from these instead of
    // re-choosing from the current declaration.
    private readonly Dictionary<string, LockedOrigin> _locked = new(StringComparer.Ordinal);
    private readonly string _rid;

    public OriginSource(IEnumerable<OriginSpec> specs, string root, string rid, string? storeRoot = null)
    {
        _specs = specs.ToDictionary(spec => spec.Name, StringComparer.Ordinal);
        Root = root;
        _rid = rid;
        StoreRoot = storeRoot;
    }

    public string Id => OriginPaths.SourceId;

    public string Root { get; }

    public string? StoreRoot { get; }

    public SeiCatalog? Catalog => null;

    public IndexRoot Index => new()
    {
        Sei = _specs.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(),
    };

    public SeiIndex? GetSei(string name)
        => _specs.TryGetValue(name, out var spec) ? Build(spec) : null;

    // Used by `add` before the declaration exists; reads the name only. The
    // path may name a snapshot when the original is gone.
    public static string ReadName(
        string path,
        string? storePath = null,
        string? declaredName = null,
        string? lockedDigest = null)
    {
        string? name = declaredName;
        if (!File.Exists(path) && storePath is not null)
        {
            // A declaration or lock already names the sei; only an unknown path
            // has to be identified from the snapshots themselves.
            if (declaredName is not null && lockedDigest is not null)
            {
                var exact = OriginSnapshot.SnapshotPath(storePath, declaredName, lockedDigest);
                if (File.Exists(exact))
                {
                    path = exact;
                }
            }

            if (!File.Exists(path))
            {
                path = SnapshotByStem(storePath, path, declaredName, out name) ?? path;
            }
        }

        var manifest = ReadManifestHeader(path);
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

    // Used by `--frozen` and rollback: the lock names the path, the bytes and
    // the artifact conditions, so all three are bound rather than re-read.
    public void PreferLocked(string name, LockedSei locked)
    {
        var digest = Hashing.NormalizeSha256(locked.Artifact.Sha256);
        _preferred[name] = digest;
        _locked[name] = new LockedOrigin(locked.OriginPath!, digest, locked.Artifact);
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
        var sourcePath = File.Exists(source.Url) ? source.Url : SnapshotFor(name, source.Sha256);
        if (sourcePath is null || !File.Exists(sourcePath))
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

    private string? SnapshotFor(string name, string sha256)
    {
        if (StoreRoot is null)
        {
            return null;
        }

        // A replay names the bytes; only a live resolution may fall back to
        // whatever snapshot happens to exist.
        if (_preferred.TryGetValue(name, out var preferred))
        {
            var exact = OriginSnapshot.SnapshotPath(StoreRoot, name, preferred);
            return File.Exists(exact) ? exact : null;
        }

        if (sha256.Length > 0)
        {
            var current = OriginSnapshot.SnapshotPath(StoreRoot, name, sha256);
            if (File.Exists(current))
            {
                return current;
            }
        }

        // `hello-*.sei` also matches `hello-tools-<sha>.sei`, so the name is
        // checked exactly (digest is last; a name may contain '-').
        var directory = OriginSnapshot.Folder(StoreRoot);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.sei")
                .Where(candidate => string.Equals(
                    OriginSnapshot.NameOf(candidate),
                    name,
                    StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private SeiIndex Build(OriginSpec spec)
    {
        // A replay names the file, so its path replaces whatever the current
        // declaration would pick — including when several selectors match.
        return _locked.TryGetValue(spec.Name, out var locked)
            ? BuildLocked(spec, locked)
            : BuildChosen(spec);

    }

    // A replay uses exactly what the lock pinned: the stored path, its digest
    // and the artifact conditions of that entry. Nothing is re-chosen from the
    // current declaration, so a declaration edited since cannot shift it.
    private SeiIndex BuildLocked(OriginSpec spec, LockedOrigin locked)
    {
        var path = LockedFile(spec.Name, locked.Path, locked.Digest);
        var manifest = ReadManifest(path, spec.Name);
        var artifact = manifest.Artifact!;
        return new SeiIndex
        {
            Name = manifest.Name,
            OriginPaths = spec.Paths,
            OriginSelectors = spec.Selectors,
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
                            Rid = locked.Artifact.Rid,
                            Prerequisites = locked.Artifact.Prerequisites,
                            Dependencies = locked.Artifact.Dependencies,
                            OptionalPeers = locked.Artifact.OptionalPeers,
                            Conflicts = locked.Artifact.Conflicts,
                            Layout = artifact.Layout,
                            Modes = artifact.Modes,
                            Source = new ArtifactLocation
                            {
                                Url = path,
                                Sha256 = locked.Digest,
                                Size = new FileInfo(path).Length,
                            },
                            OriginPath = locked.Path,
                        },
                    ],
                },
            ],
        };
    }

    // The original if it is still there — verified against the lock, because a
    // warm cache would otherwise never read it — otherwise the snapshot with
    // exactly that digest (ADR-0044).
    private string LockedFile(string name, string stored, string digest)
    {
        var path = OriginPaths.Resolve(stored, Root);
        if (File.Exists(path))
        {
            var actual = Hashing.Sha256File(path);
            if (string.Equals(actual, digest, StringComparison.Ordinal))
            {
                return path;
            }

            throw new SeikoException(
                $"{name}: the origin file changed since it was locked "
                + $"({digest} locked, {actual} read)",
                "checksum")
            {
                Hint = "rebuild under a new version, or run `sk remove` and add the file again",
                HintKey = "hint.origin-changed",
            };
        }

        if (StoreRoot is not null)
        {
            var snapshot = OriginSnapshot.SnapshotPath(StoreRoot, name, digest);
            if (File.Exists(snapshot))
            {
                return snapshot;
            }
        }

        throw new SeikoException(
            $"{name}: the origin file is gone and {digest} has no snapshot",
            "checksum");
    }

    private SeiIndex BuildChosen(OriginSpec spec)
    {
        var chosen = Choose(spec);
        var artifacts = new List<ArtifactEntry>();
        SeiManifest? first = null;
        foreach (var (stored, selector) in chosen)
        {
            var path = OriginPaths.Resolve(stored, Root);
            // Resolution reads the manifest too, so a missing original has to
            // fall back before the download stage.
            if (!File.Exists(path))
            {
                path = SnapshotFor(spec.Name, string.Empty) ?? path;
            }

            if (!File.Exists(path))
            {
                throw new SeikoException(
                    $"{spec.Name}: the origin file is gone and no snapshot matches"
                    + (_preferred.TryGetValue(spec.Name, out var wanted)
                        ? $"; {wanted} has no snapshot"
                        : "; add it again"),
                    "checksum");
            }

            var manifest = ReadManifest(path, spec.Name);
            if (ExternalFiles.Count(manifest.Artifact) > 0)
            {
                throw new SeikoException(
                    $"{manifest.Name}: a local file cannot serve external files",
                    "source");
            }

            if (first is null)
            {
                first = manifest;
            }
            else if (!string.Equals(first.Version, manifest.Version, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"{spec.Name}: origin files declare different versions "
                    + $"('{first.Version}' and '{manifest.Version}')",
                    "resolve");
            }

            var artifact = manifest.Artifact!;
            artifacts.Add(new ArtifactEntry
            {
                // The selector already filtered by rid; this is the manifest's
                // own field, so the store's index cross-check still holds.
                Rid = artifact.Rid,
                Prerequisites = Merge(artifact.Prerequisites, selector.Prerequisites),
                Dependencies = artifact.Dependencies,
                OptionalPeers = artifact.OptionalPeers,
                Conflicts = artifact.Conflicts,
                Layout = artifact.Layout,
                Modes = artifact.Modes,
                Source = new ArtifactLocation
                {
                    Url = path,
                    Sha256 = Hashing.Sha256File(path),
                    Size = new FileInfo(path).Length,
                },
                OriginPath = stored,
            });
        }

        return new SeiIndex
        {
            Name = spec.Name,
            OriginPaths = spec.Paths,
            OriginSelectors = spec.Selectors,
            Versions =
            [
                new SeiVersionEntry
                {
                    Version = first!.Version,
                    Dependencies = first.Dependencies,
                    Prerequisites = first.Prerequisites,
                    OptionalPeers = first.OptionalPeers,
                    Conflicts = first.Conflicts,
                    Artifacts = artifacts,
                },
            ],
        };
    }

    // Both sets must hold for the file to apply, so the artifact carries the
    // union and the resolver evaluates it like any other condition. A name in
    // both keeps both ranges: dropping one would silently widen the condition.
    private static List<SeiDependency> Merge(
        List<SeiDependency> intrinsic,
        List<SeiDependency> fromSelector)
    {
        var merged = intrinsic
            .Select(item => new SeiDependency { Name = item.Name, Version = item.Version })
            .ToList();
        merged.AddRange(fromSelector.Select(
            item => new SeiDependency { Name = item.Name, Version = item.Version }));
        return merged;
    }

    // Every selector whose rid matches is a candidate; the resolver's own
    // prerequisite matching then chooses between them, so a file whose
    // condition does not hold is never installed. Two candidates with the same
    // normalized condition leave it no way to choose (ADR-0044).
    private List<(string Path, OriginSelector Selector)> Choose(OriginSpec spec)
    {
        var matches = new List<(string Path, OriginSelector Selector)>();
        for (var i = 0; i < spec.Paths.Count; i++)
        {
            var selector = spec.Selectors[i];
            if (string.IsNullOrWhiteSpace(selector.Rid)
                || string.Equals(selector.Rid, "*", StringComparison.Ordinal)
                || string.Equals(selector.Rid, _rid, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((spec.Paths[i], selector));
            }
        }

        if (matches.Count == 0)
        {
            throw new SeikoException(
                $"{spec.Name}: no origin file matches rid '{_rid}'",
                "resolve");
        }

        for (var i = 1; i < matches.Count; i++)
        {
            if (string.Equals(
                ArtifactConditions.Key(matches[0].Selector.Rid, matches[0].Selector.Prerequisites),
                ArtifactConditions.Key(matches[i].Selector.Rid, matches[i].Selector.Prerequisites),
                StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"{spec.Name}: origin paths '{matches[0].Path}' and '{matches[i].Path}' have the same condition",
                    "resolve");
            }
        }

        return matches;
    }

    private sealed record LockedOrigin(string Path, string Digest, LockedArtifact Artifact);

    private SeiManifest ReadManifest(string path, string name)
    {
        var manifest = ReadManifestHeader(path)
            ?? throw new SeikoException($"{Path.GetFileName(path)}: sei.json is empty", "io");

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

    // Used only for an unknown path: every snapshot is read until one declares
    // a name the stem can belong to. A prefix match alone is not enough, since
    // `hello` is a prefix of `hello-tools`; the manifest decides, and an
    // ambiguous stem is refused rather than resolved by timestamp.
    private static string? SnapshotByStem(
        string storePath,
        string path,
        string? declaredName,
        out string? name)
    {
        var folder = OriginSnapshot.Folder(storePath);
        if (!Directory.Exists(folder))
        {
            name = null;
            return null;
        }

        var matches = new List<string>();
        foreach (var candidate in Directory.EnumerateFiles(folder, "*.sei"))
        {
            SeiManifest? manifest;
            try
            {
                manifest = ReadManifestHeader(candidate);
            }
            catch (SeikoException)
            {
                continue;
            }

            if (manifest is null || string.IsNullOrEmpty(manifest.Name))
            {
                continue;
            }

            if (string.Equals(manifest.Name, declaredName, StringComparison.Ordinal)
                || (declaredName is null && OriginPaths.StemMatches(path, manifest.Name)))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            name = null;
            return null;
        }

        name = ReadManifestHeader(matches[0])!.Name;

        // Two different names can both fit the stem (`hello` and `hello-tools`
        // for hello-tools-win); the most specific one wins. Snapshots of the
        // same name are not ambiguous, so they collapse to the newest.
        var names = matches
            .ToDictionary(
                candidate => candidate,
                candidate => ReadManifestHeader(candidate)!.Name,
                StringComparer.Ordinal);
        var distinct = names.Values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            // Keep the names no other name is a prefix of: `hello-tools`
            // survives `hello`, since the file stem starts with the longer one.
            var wider = distinct
                .Where(value => distinct.All(other => other == value
                    || !other.StartsWith(value + "-", StringComparison.Ordinal)))
                .ToList();
            if (wider.Count != 1)
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)} matches several origin names ({string.Join(", ", wider)}); "
                    + "rename it so only one applies",
                    "usage");
            }

            distinct = wider;
        }

        var chosenName = distinct[0];
        name = chosenName;
        return matches
            .Where(candidate => string.Equals(names[candidate], chosenName, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    // Reads the root manifest entry only. The size limit applies to that entry,
    // not to the package (ADR-0024 limits the manifest read on its own).
    private static SeiManifest? ReadManifestHeader(string path)
    {
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

            if (manifests[0].Length > ArchiveLimits.MaxManifestBytes)
            {
                throw new SeikoException(
                    $"{Path.GetFileName(path)}: sei.json exceeds the {ArchiveLimits.MaxManifestBytes}-byte limit",
                    "checksum");
            }

            try
            {
                using var stream = manifests[0].Open();
                return JsonSerializer.Deserialize(stream, SeikoJsonContext.Default.SeiManifest);
            }
            catch (JsonException ex)
            {
                throw new SeikoException($"{Path.GetFileName(path)}: invalid sei.json ({ex.Message})", "io");
            }
        }
    }
}
