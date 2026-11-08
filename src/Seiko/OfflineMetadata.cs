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
        SeikoLock? lockedGraph = null)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var httpSources = config.Sources
            .Where(IsHttp)
            .Select(source => (Source: source, IndexUri: new Uri(source.Index)))
            .ToList();
        var pending = new Queue<string>(initial);
        if (lockedGraph is not null)
        {
            // A replay is fully described by the lock: every name it needs is
            // a locked entry. Relations of unselected versions, unselected
            // artifacts and non-retaining kinds would only be extra cache
            // requirements, so the walk does not follow them here.
            foreach (var locked in lockedGraph.Sei)
            {
                pending.Enqueue(locked.Name);
            }
        }

        // A name served by an origin never consults a source, so a source
        // index is only required when some reached name has to come from one.
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!SeiName.IsValid(name) || !seen.Add(name))
            {
                continue;
            }

            // The origin answers this name from disk, so no source is consulted
            // — but its own dependencies still are, so they are enqueued here
            // instead of being read from an index.
            if (originNames.Contains(name))
            {
                if (originIndexes.TryGetValue(name, out var originSpec))
                {
                    foreach (var stored in originSpec.Paths)
                    {
                        var full = LocalSource.ResolvePath(stored, root);
                        if (!File.Exists(full))
                        {
                            // The declaration may still be satisfied by a
                            // snapshot; the source reports it if not.
                            continue;
                        }

                        if (OriginManifest.Dependencies(full) is { } manifest)
                        {
                            Enqueue(manifest, pending);
                        }
                    }
                }

                continue;
            }

            SeiIndex? found = null;
            foreach (var source in config.Sources)
            {
                if (IsHttp(source))
                {
                    var uri = new Uri(new Uri(source.Index), $"sei/{name}.json");
                    var path = MetadataCache.PathFor(storePath, uri.AbsoluteUri);
                    // A recorded 404 is an answer, not a missing entry.
                    if (MetadataCache.IsMissing(path))
                    {
                        continue;
                    }

                    if (!File.Exists(path))
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}'");
                        continue;
                    }

                    found = MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex);
                    if (found is null)
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}' (unreadable)");
                    }

                    break;
                }

                // A local miss must keep looking: the resolver and the search
                // walk fall through to later sources too.
                found = LocalIndex(source, name, root);
                if (found is not null)
                {
                    break;
                }

                continue;
            }

            if (found is null)
            {
                continue;
            }

            // A replay only needs the index of each locked name, not the
            // relations of its selected version: the lock already names every
            // target that will be installed.
            if (lockedGraph is not null)
            {
                continue;
            }

            Enqueue(found, pending);
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

    // Local sources are always readable; their files still contribute relation
    // names, and a missing local file is reported by the source itself later.
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

    private static void Enqueue(SeiIndex index, Queue<string> pending)
    {
        foreach (var entry in index.Versions ?? [])
        {
            EnqueueEntry(entry, pending);
        }
    }

    private static void EnqueueEntry(SeiVersionEntry entry, Queue<string> pending)
    {
        foreach (var relation in entry.Dependencies
            .Concat(entry.Prerequisites)
            .Concat(entry.OptionalPeers)
            .Concat(entry.Conflicts))
        {
            pending.Enqueue(relation.Name);
        }

        foreach (var artifact in entry.Artifacts ?? [])
        {
            foreach (var relation in (artifact?.Dependencies ?? [])
                .Concat(artifact!.Prerequisites ?? [])
                .Concat(artifact.OptionalPeers ?? [])
                .Concat(artifact.Conflicts ?? []))
            {
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
                        Artifacts = [],
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
