using NuGet.Versioning;

namespace Seiko;

// Offline mode may only use cached metadata and stored artifacts. Missing
// pieces are collected up front so the user sees one list instead of the
// first failure, and nothing is downloaded implicitly.
internal static class OfflineMetadata
{
    public static void Verify(
        SeikoConfig config,
        string storePath,
        string root,
        IEnumerable<string> initial,
        SeikoLock? lockedGraph = null,
        bool allowPrerelease = false,
        bool allowCritical = false)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var httpSources = config.Sources
            .Where(IsHttp)
            .Select(source => (Source: source, IndexUri: new Uri(source.Index)))
            .ToList();
        var pending = new Queue<string>(initial);
        // Every range any enqueued edge imposes on a name, mirroring what the
        // resolver's pending list would hold. It grows as edges are added, so
        // a transitive range prunes as reliably as a declared one.
        var ranges = new Dictionary<string, List<NuGet.Versioning.VersionRange>>(StringComparer.Ordinal);
        void Impose(string name, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)
                || text.Trim() == "*"
                || !NuGet.Versioning.VersionRange.TryParse(text, out var parsed))
            {
                return;
            }

            if (!ranges.TryGetValue(name, out var list))
            {
                ranges[name] = list = [];
            }

            list.Add(parsed);
        }

        if (lockedGraph is not null)
        {
            // A replay is fully described by the lock: every name it needs is
            // a locked entry. Relations of unselected versions, unselected
            // artifacts and non-retaining kinds would only be extra cache
            // requirements, so the walk does not follow them here, and origin
            // files are not expanded for the same reason.
            foreach (var locked in lockedGraph.Sei)
            {
                pending.Enqueue(locked.Name);
            }
        }

        // A name served by an origin never consults a source, so a source
        // index is only required when some reached name has to come from one.
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? SeikoClient.DefaultRid() : config.Rid;
        var originIndexes = config.Origin.ToDictionary(origin => origin.Name, StringComparer.Ordinal);
        var originNames = originIndexes.Keys.ToHashSet(StringComparer.Ordinal);
        var needsIndex = pending.Any(name => !originNames.Contains(name));

        if (needsIndex)
        {
            foreach (var (source, indexUri) in httpSources)
            {
                var path = MetadataCache.PathFor(storePath, indexUri.AbsoluteUri);
                if (!File.Exists(path))
                {
                    missing.Add($"index metadata of source '{source.Id}'");
                }
            }
        }

        foreach (var spec in config.Sei)
        {
            Impose(spec.Name, spec.Version);
        }
        // A yanked version stays a candidate when the lock already holds it.
        var lockedVersions = (lockedGraph?.Sei ?? [])
            .Where(sei => NuGet.Versioning.NuGetVersion.TryParse(sei.Version, out _))
            .ToDictionary(
                sei => sei.Name,
                sei => NuGet.Versioning.NuGetVersion.Parse(sei.Version),
                StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pendingOrigin = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!SeiName.IsValid(name) || !seen.Add(name))
            {
                continue;
            }

            // The origin answers this name from disk, so no source is consulted
            // — but its dependencies still are, so they are enqueued here
            // instead of being read from an index. A replay only follows the
            // locked graph, which already names every target.
            if (originNames.Contains(name))
            {
                // Expanded after the walk instead: a selector may need a name a
                // later source only reveals, so one pass here would depend on
                // declaration order.
                pendingOrigin.Add(name);
                continue;
            }

            CollectName(name);
        }

        // Looks one name up in the configured sources and queues what it
        // depends on. Shared by the first pass and the origin fixpoint below.
        void CollectName(string lookup)
        {
            SeiIndex? found = null;
            foreach (var source in config.Sources)
            {
                if (IsHttp(source))
                {
                    var uri = new Uri(new Uri(source.Index), $"sei/{lookup}.json");
                    var path = MetadataCache.PathFor(storePath, uri.AbsoluteUri);
                    // A recorded 404 is an answer, not a missing entry.
                    if (MetadataCache.IsMissing(path))
                    {
                        continue;
                    }

                    if (!File.Exists(path))
                    {
                        missing.Add($"sei/{lookup}.json from source '{source.Id}'");
                        continue;
                    }

                    found = MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex);
                    if (found is null)
                    {
                        missing.Add($"sei/{lookup}.json from source '{source.Id}' (unreadable)");
                    }

                    break;
                }

                // A local miss must keep looking: the resolver and the search
                // walk fall through to later sources too.
                found = LocalIndex(source, lookup, root);
                if (found is not null)
                {
                    break;
                }

                continue;
            }

            if (found is null)
            {
                return;
            }

            // A replay only needs the index of each locked name, not the
            // relations of its selected version: the lock already names every
            // target that will be installed.
            if (lockedGraph is not null)
            {
                return;
            }

            Enqueue(
                found,
                pending,
                rid,
                ranges.TryGetValue(lookup, out var imposed) ? imposed : [],
                allowPrerelease,
                allowCritical,
                lockedVersions.TryGetValue(lookup, out var locked) ? locked : null,
                lookup,
                Impose);
        }

        // Origin entries last, repeated until the graph stops growing: only then
        // is every selector judged against the full set of names, so swapping
        // two declarations cannot change the answer (ADR-0027).
        if (lockedGraph is null)
        {
            var rangesOf = RangesOf(config);
            var handled = new HashSet<string>(seen, StringComparer.Ordinal);
            while (true)
            {
                // Rescanned every round: a name a later iteration discovers may
                // make an earlier origin's selector apply, so expansion cannot
                // be a one-shot set (ADR-0027: order must not matter).
                var before = seen.Count;
                foreach (var name in pendingOrigin)
                {
                    if (!originIndexes.TryGetValue(name, out var originSpec))
                    {
                        continue;
                    }

                    foreach (var full in OriginFiles(originSpec, root, rid, storePath, seen, rangesOf))
                    {
                        if (OriginManifest.Dependencies(full) is not { } manifest)
                        {
                            continue;
                        }

                        var added = new Queue<string>();
                        Enqueue(manifest, added, rid, [], allowPrerelease, allowCritical, null, name, Impose);
                        foreach (var dependency in added)
                        {
                            if (seen.Add(dependency))
                            {
                                pending.Enqueue(dependency);
                            }
                        }
                    }
                }

                // Dependencies found here may be source names that reveal more
                // names, so drain the queue through the same walk. `handled`
                // is what bounds it: a cycle re-queues the same names forever,
                // and `seen` cannot do the job because origin-pulled deps are
                // already in it before they are walked.
                while (pending.Count > 0)
                {
                    var next = pending.Dequeue();
                    // Only `handled` decides: a name pulled from an origin is
                    // already in `seen` (that is how it was deduplicated on
                    // the way in), so requiring `seen.Add` here would silently
                    // drop every one of them.
                    if (!SeiName.IsValid(next) || !handled.Add(next))
                    {
                        continue;
                    }

                    seen.Add(next);

                    if (originNames.Contains(next))
                    {
                        pendingOrigin.Add(next);
                        continue;
                    }

                    CollectName(next);
                }

                if (seen.Count == before)
                {
                    break;
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new SeikoException(
                $"offline: missing cached metadata: {string.Join("; ", missing)}. "
                + "Run the command once online, then retry offline",
                "source");
        }
    }

    // Search walks each source's own root list, so offline readiness must keep
    // the source association: only the catalog or per-name data for that
    // (source, name) pair is actually visited. A source that never lists the
    // name must not make the offline run require its cache.
    public static void VerifySearch(SeikoConfig config, string storePath, string root)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in config.Sources)
        {
            if (IsHttp(source))
            {
                var indexUri = new Uri(source.Index);
                var indexPath = MetadataCache.PathFor(storePath, indexUri.AbsoluteUri);
                if (!File.Exists(indexPath))
                {
                    missing.Add($"index metadata of source '{source.Id}'");
                    continue;
                }

                var index = MetadataCache.TryRead(indexPath, SeikoJsonContext.Default.IndexRoot);
                if (index is null)
                {
                    missing.Add($"index metadata of source '{source.Id}' (unreadable)");
                    continue;
                }

                var catalogPath = MetadataCache.PathFor(
                    storePath,
                    new Uri(indexUri, CatalogFile.FileName).AbsoluteUri);
                var catalogNames = CatalogFile.TryRead(catalogPath)?.Sei
                    .Select(entry => entry.Name)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var name in index.Sei ?? [])
                {
                    if (!SeiName.IsValid(name) || seen.Contains(name))
                    {
                        continue;
                    }

                    if (catalogNames?.Contains(name) == true)
                    {
                        seen.Add(name);
                        continue;
                    }

                    var namePath = MetadataCache.PathFor(
                        storePath,
                        new Uri(indexUri, $"sei/{name}.json").AbsoluteUri);
                    // A recorded 404 is an answer, not a missing entry; a later
                    // source may still own the name.
                    if (MetadataCache.IsMissing(namePath))
                    {
                        continue;
                    }

                    if (!File.Exists(namePath))
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}'");
                        continue;
                    }

                    if (MetadataCache.TryRead(namePath, SeikoJsonContext.Default.SeiIndex) is null)
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}' (unreadable)");
                        continue;
                    }

                    seen.Add(name);
                }

                continue;
            }

            // Local sources read directly; a missing file simply lets a later
            // source answer, matching the online walk.
            var localIndexPath = LocalSource.ResolvePath(source.Index, root);
            var localIndex = MetadataCache.TryRead(localIndexPath, SeikoJsonContext.Default.IndexRoot);
            if (localIndex is null)
            {
                continue;
            }

            var baseDirectory = Path.GetDirectoryName(localIndexPath) ?? root;
            // Search asks the local catalog before the per-name file, so a
            // catalog hit answers the name and later sources need no cache.
            var localCatalogNames = CatalogFile.TryRead(
                    Path.Combine(baseDirectory, CatalogFile.FileName))?
                .Sei.Select(entry => entry.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var name in localIndex.Sei ?? [])
            {
                if (!SeiName.IsValid(name) || seen.Contains(name))
                {
                    continue;
                }

                if (localCatalogNames?.Contains(name) == true
                    || File.Exists(Path.Combine(baseDirectory, "sei", name + ".json")))
                {
                    seen.Add(name);
                }
            }
        }

        if (missing.Count > 0)
        {
            throw new SeikoException(
                $"offline: missing cached metadata: {string.Join("; ", missing)}. "
                + "Run the command once online, then retry offline",
                "source");
        }
    }

    // Every declared file whose condition fits this platform, whether the
    // original or a stored snapshot answers it. Making the requirement on an
    // origin name at least as broad as the resolver's is the point; a missing
    // file is still reported by the source itself later.
    private static IEnumerable<string> OriginFiles(
        OriginSpec spec,
        string root,
        string rid,
        string? storeRoot,
        ISet<string> known,
        Func<string, string?> rangesOf)
    {
        for (var i = 0; i < spec.Paths.Count; i++)
        {
            var selector = i < spec.Selectors.Count ? spec.Selectors[i] : null;
            if (selector is not null
                && !string.IsNullOrWhiteSpace(selector.Rid)
                && !string.Equals(selector.Rid, "*", StringComparison.Ordinal)
                && !string.Equals(selector.Rid, rid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A condition on a package that is not in the graph and could not
            // be supplied by a dependency of the candidate file itself cannot
            // hold; a false here must not skip a file that would pull its own
            // prerequisite in, so `Holds` is asked with the file's own names.
            var full = LocalSource.ResolvePath(spec.Paths[i], root);
            var candidate = File.Exists(full)
                ? full
                : storeRoot is null ? full : OriginSnapshotPath(storeRoot, spec.Name) ?? full;
            if (selector is not null && !Holds(selector, known, rangesOf, spec.Paths[i], root, candidate))
            {
                continue;
            }

            // The original is preferred; a deleted one is answered by a
            // snapshot, if one is still there.
            if (candidate == full)
            {
                yield return full;
                continue;
            }

            if (storeRoot is not null && OriginSnapshotPath(storeRoot, spec.Name) is { } snapshot)
            {
                yield return snapshot;
            }
        }
    }

    // A selector can only apply if every package it names is in the graph and
    // the ranges intersect. Both sides are ranges, so the selector range is
    // parsed as one rather than as a concrete version; an empty intersection
    // means the resolver could never pick that file.
    private static bool Holds(
        OriginSelector selector,
        ISet<string> graph,
        Func<string, string?> rangeOf,
        string stored,
        string root,
        string candidate)
    {
        // Names the candidate itself declares still count as reachable, even
        // though they are not in the queue yet — at the package and at the
        // artifact level, since both feed the graph (ADR-0027, ADR-0044).
        var own = OriginManifest.Dependencies(candidate)
            ?.Versions.SelectMany(version => version.Dependencies
                .Concat(version.Artifacts.SelectMany(artifact => artifact?.Dependencies ?? []))
                .Select(relation => relation.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in selector.Prerequisites)
        {
            if (!graph.Contains(item.Name) && own?.Contains(item.Name) != true)
            {
                return false;
            }

            if (item.Version is not { } selectorRange
                || rangeOf(item.Name) is not { } declaredRange
                || !NuGet.Versioning.VersionRange.TryParse(selectorRange, out var wanted)
                || !NuGet.Versioning.VersionRange.TryParse(declaredRange, out var declared))
            {
                continue;
            }

            // An empty intersection is the only way the pair cannot hold; the
            // resolver still does the real search later.
            if (NuGet.Versioning.VersionRange.CommonSubSet([declared, wanted]) is null)
            {
                return false;
            }
        }

        return true;
    }

    // A stored snapshot beats nothing: the file name carries the digest, so a
    // rebuilt original cannot shadow it. The newest one is the best guess for
    // The declared range of a directly declared name, if it has one.
    private static Func<string, string?> RangesOf(SeikoConfig config)
        => name => config.Sei
            .FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal))?
            .Version;

    // A stored snapshot beats nothing. The name is matched exactly, never by
    // glob: `hello-*.sei` would also catch `hello-tools-<sha>.sei`. Newest
    // wins because this only lists dependencies; the resolver checks the real
    // digest later.
    private static string? OriginSnapshotPath(string storeRoot, string name)
    {
        var folder = OriginSnapshot.Folder(storeRoot);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, "*.sei")
            .Where(file => string.Equals(OriginSnapshot.NameOf(file), name, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static SeiIndex? LocalIndex(SourceConfig source, string name, string root)
    {
        // Same path rule as LocalSource; relative indexes belong to the
        // install root, not the process working directory.
        var indexPath = LocalSource.ResolvePath(source.Index, root);
        var baseDirectory = Path.GetDirectoryName(indexPath) ?? root;
        var path = Path.Combine(baseDirectory, "sei", name + ".json");
        return File.Exists(path)
            ? MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex)
            : null;
    }
    public static void EnsureArtifacts(Store store, IReadOnlyList<ResolvedSei> resolved)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sei in resolved.Where(sei => IsHttpUri(sei.Source)))
        {
            try
            {
                var stored = store.TryGet(sei.Artifact.Source.Sha256);
                if (stored is null
                    || !string.Equals(stored.Manifest.Name, sei.Name, StringComparison.Ordinal)
                    || !NuGetVersion.TryParse(stored.Manifest.Version, out var version)
                    || !version.Equals(sei.ParsedVersion))
                {
                    missing.Add($"{sei.Name} {sei.Version}");
                }
            }
            catch (SeikoException ex) when (ex.Code == "cache-repair")
            {
                missing.Add($"{sei.Name} {sei.Version}");
            }
        }

        if (missing.Count > 0)
        {
            throw new SeikoException(
                $"offline: missing cached artifacts: {string.Join(", ", missing)}. "
                + "Run `sk sync` while online to download them",
                "source");
        }
    }

    // Versions are filtered the way the resolver filters candidates: by the
    // declared ranges, pre-release allowance and yank state. Without an index
    // entry (an origin manifest) only the single version exists.
    private static void Enqueue(
        SeiIndex index,
        Queue<string> pending,
        string rid,
        IReadOnlyList<NuGet.Versioning.VersionRange> constraints,
        bool allowPrerelease,
        bool allowCritical,
        NuGet.Versioning.NuGetVersion? lockedVersion,
        string name,
        Action<string, string?> impose)
    {
        foreach (var entry in index.Versions ?? [])
        {
            if (!NuGet.Versioning.NuGetVersion.TryParse(entry.Version, out var version))
            {
                continue;
            }

            // The same gate the resolver applies before a version can become a
            // candidate: every pending range must admit it, pre-releases only
            // when allowed or when a range asks for them, and neither yanked
            // nor critical versions unless they are the locked one or critical
            // is explicitly allowed (Resolver.Candidates).
            if (constraints.Count > 0 && !constraints.All(constraint => constraint.Satisfies(version)))
            {
                continue;
            }

            if (version.IsPrerelease
                && !allowPrerelease
                && !constraints.Any(Resolver.RangeAllowsPrerelease))
            {
                continue;
            }

            if (entry.Yanked && !version.Equals(lockedVersion))
            {
                continue;
            }

            if (Retirement.IsCritical(entry) && !allowCritical)
            {
                continue;
            }

            EnqueueEntry(entry, pending, rid, impose);
        }
    }

    // The resolver only ever puts a dependency into the graph; prerequisites,
    // optional peers and conflicts are merely checked against what is already
    // assigned, so their indexes are never read (Resolver, ADR-0027). Only
    // artifacts that fit this platform are considered, matching selection.
    private static void EnqueueEntry(
        SeiVersionEntry entry,
        Queue<string> pending,
        string rid,
        Action<string, string?> impose)
    {
        foreach (var relation in entry.Dependencies)
        {
            impose(relation.Name, relation.Version);
            pending.Enqueue(relation.Name);
        }

        foreach (var artifact in (entry.Artifacts ?? []).Where(artifact => artifact is not null
            && (string.IsNullOrWhiteSpace(artifact.Rid)
                || artifact.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var relation in artifact!.Dependencies)
            {
                impose(relation.Name, relation.Version);
                pending.Enqueue(relation.Name);
            }
        }
    }

    private static bool IsHttp(SourceConfig source) => IsHttpUri(source.Index);

    private static bool IsHttpUri(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUri(ISeiSource source) => source is HttpSource;
}

// Reads only the relations of an origin artifact, so an offline run can list
// what the local dependency graph needs before it starts (ADR-0044). A file
// that cannot be read is left to the source, which reports the real problem.
internal static class OriginManifest
{
    // Relations of the package and of the artifact both count; the artifact
    // set is what a selector adds on top of the declaration.
    public static SeiIndex? Dependencies(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(
                item => ArchiveLimits.NormalizeName(item.FullName) == "sei.json");
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                stream,
                SeikoJsonContext.Default.SeiManifest);
            if (manifest is null)
            {
                return null;
            }

            // Artifact relations live on the artifact, so they are carried as
            // the only artifact rather than flattened into the version.
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
                                Rid = manifest.Artifact?.Rid,
                                Dependencies = manifest.Artifact?.Dependencies ?? [],
                                Prerequisites = manifest.Artifact?.Prerequisites ?? [],
                                OptionalPeers = manifest.Artifact?.OptionalPeers ?? [],
                                Conflicts = manifest.Artifact?.Conflicts ?? [],
                            },
                        ],
                    },
                ],
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
