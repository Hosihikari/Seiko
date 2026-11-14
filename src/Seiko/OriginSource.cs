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
        string storedPath,
        string root,
        string? storePath = null,
        string? declaredName = null)
    {
        // A declaration pinned to this very path is the identity; a file name
        // can fit more than one sei, so it is never re-derived. A declaration
        // for a different path says nothing about these bytes.
        if (declaredName is not null)
        {
            return declaredName;
        }

        var path = OriginPaths.Resolve(storedPath, root);
        if (!File.Exists(path) && storePath is not null)
        {
            // Only an unknown path has to be identified from the snapshots
            // themselves.
            path = SnapshotByStem(storePath, path) ?? path;
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

        // The lock records the merged artifact scope. Rebuild the entry's
        // artifact-level relations by removing the selected group's share, so
        // the resolver merges exactly the same lists again (ADR-0045).
        var selected = locked.Artifact.Group is { } groupIndex
            && artifact.DependencyGroups is { } groups
            && groupIndex < groups.Count
                ? groups[groupIndex]
                : null;

        // Subtracting the selected group from the lock's merged scope only
        // recovers the artifact-level share when the index is the one that
        // was locked. A changed index would otherwise rebuild a share that
        // happens to match the lock's own merged lists, so the recovered
        // share is checked against the file's declaration (ADR-0045).
        var recovered = new ArtifactManifest
        {
            Dependencies = Without(locked.Artifact.Dependencies, selected?.Dependencies),
            Prerequisites = Without(locked.Artifact.Prerequisites, selected?.Prerequisites),
            OptionalPeers = Without(locked.Artifact.OptionalPeers, selected?.OptionalPeers),
            Conflicts = Without(locked.Artifact.Conflicts, selected?.Conflicts),
        };
        // The locked selector's own prerequisites are part of the recorded
        // artifact-level share, so they are the comparison base on top of the
        // file's declaration.
        var selectorIndex = spec.Paths.FindIndex(path =>
            string.Equals(path, locked.Path, StringComparison.Ordinal));
        var selector = selectorIndex >= 0 && selectorIndex < spec.Selectors.Count
            ? spec.Selectors[selectorIndex]
            : null;
        var expectedPrerequisites = Merge(artifact.Prerequisites, selector?.Prerequisites ?? []);
        if (!SameRelations(recovered.Dependencies, artifact.Dependencies)
            || !SameRelations(recovered.Prerequisites, expectedPrerequisites)
            || !SameRelations(recovered.OptionalPeers, artifact.OptionalPeers)
            || !SameRelations(recovered.Conflicts, artifact.Conflicts))
        {
            throw new SeikoException(
                $"{spec.Name}: the locked dependency group does not match the file's groups "
                + "(the file changed since it was locked)",
                "checksum");
        }

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
                            Prerequisites = recovered.Prerequisites,
                            Dependencies = recovered.Dependencies,
                            OptionalPeers = recovered.OptionalPeers,
                            Conflicts = recovered.Conflicts,
                            Layout = artifact.Layout,
                            Modes = artifact.Modes,
                            Preserve = artifact.Preserve,
                            DependencyGroups = artifact.DependencyGroups,
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
                Preserve = artifact.Preserve,
                DependencyGroups = artifact.DependencyGroups,
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
    private static bool SameRelations(
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

    // Multiset subtraction: each group relation removes one matching entry
    // from the merged lock lists, leaving the artifact-level share.
    private static List<SeiDependency> Without(
        List<SeiDependency> merged,
        List<SeiDependency>? scoped)
    {
        var result = merged
            .Select(item => new SeiDependency { Name = item.Name, Version = item.Version })
            .ToList();
        foreach (var relation in scoped ?? [])
        {
            var index = result.FindIndex(candidate =>
                string.Equals(candidate.Name, relation.Name, StringComparison.Ordinal)
                && string.Equals(candidate.Version, relation.Version, StringComparison.Ordinal));
            if (index >= 0)
            {
                result.RemoveAt(index);
            }
        }

        return result;
    }

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
        if (manifest.SchemaVersion is < 2 or > SeiManifest.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)}: unsupported sei.json schemaVersion {manifest.SchemaVersion}",
                "io");
        }

        if (manifest.SchemaVersion < 4
            && (manifest.Artifact?.Preserve is not null
                || manifest.Artifact?.DependencyGroups is not null))
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)}: sei.json schemaVersion {manifest.SchemaVersion} cannot declare preserve or dependencyGroups",
                "io");
        }

        if (manifest.Artifact is null)
        {
            throw new SeikoException($"{Path.GetFileName(path)}: sei.json has no artifact object", "io");
        }

        // A local file follows the same packaging rule as an index entry:
        // identically gated groups with different relations can only be told
        // apart by luck (ADR-0045).
        if (manifest.Artifact.DependencyGroups is { } groups
            && DependencyGroups.FindAmbiguousPair(groups) is { } ambiguous)
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)}: dependency groups {ambiguous} and an earlier one have the same conditions but different relations",
                "usage");
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
    private static string? SnapshotByStem(string storePath, string path)
    {
        var folder = OriginSnapshot.Folder(storePath);
        if (!Directory.Exists(folder))
        {
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

            if (OriginPaths.StemMatches(path, manifest.Name))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        // Two different names can both fit the stem (`hello` and `hello-tools`
        // for hello-tools-win). Picking the longer one is still a guess, so an
        // unknown path with more than one candidate is refused; snapshots of
        // the same name are not ambiguous and collapse to the newest.
        var names = matches
            .ToDictionary(
                candidate => candidate,
                candidate => ReadManifestHeader(candidate)!.Name,
                StringComparer.Ordinal);
        var distinct = names.Values.Distinct(StringComparer.Ordinal).OrderBy(
            value => value,
            StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            throw new SeikoException(
                $"{Path.GetFileName(path)} matches several origin names ({string.Join(", ", distinct)}); "
                + "name the file after the sei, or add it while its declaration exists",
                "usage");
        }

        var chosenName = distinct[0];
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
