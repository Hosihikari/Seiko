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
        IReadOnlyList<SeiSpec> specs,
        SeikoLock? lockedGraph = null,
        bool allowPrerelease = false,
        bool allowCritical = false,
        SeikoLock? existing = null,
        string rid = "",
        IReadOnlySet<string>? updateNames = null,
        bool latestInstallable = false)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        // A replay is fully described by the lock: check each locked name and
        // stop. Its relations are not re-walked, so no resolver run is needed.
        // Root indexes are not required: the replay only reads per-name
        // metadata, which is cached by URL.
        if (lockedGraph is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var locked in lockedGraph.Sei)
            {
                if (seen.Add(locked.Name)
                    && !string.Equals(locked.Source, OriginPaths.SourceId, StringComparison.Ordinal))
                {
                    if (!HasIndex(config, root, storePath, locked.Source, locked.Name))
                    {
                        missing.Add($"sei/{locked.Name}.json from source '{locked.Source}'");
                    }
                }
            }

            if (missing.Count > 0)
            {
                throw Missing(missing);
            }

            return;
        }

        // The probe runs the real resolver with the planned declarations and
        // options, so candidate admission, ranges, pre-release hints, yank and
        // critical all come from one place. A name with no cached answer is
        // recorded and stepped over instead of aborting the walk, so one pass
        // lists every reachable gap.
        var targetRid = string.IsNullOrWhiteSpace(rid) ? SeikoClient.DefaultRid() : rid;
        using var probe = new ProbeSource(config, root, storePath, targetRid);
        var resolver = new Resolver(
            [probe],
            // The real run's lock, so a yanked version it already holds stays
            // a candidate here too (never the replay branch above).
            existing,
            targetRid,
            allowPrerelease,
            updateNames,
            latestInstallable,
            allowCritical);
        SeikoException? failure = null;
        try
        {
            probe.CrossCheck(resolver.Resolve(specs));
        }
        catch (SeikoException ex) when (ex.Code is "source" or "resolve")
        {
            failure = ex;
        }

        // The gaps beat the failure: once the cache is whole the real run
        // reports the same thing. A spent search budget is the exception —
        // parts of the graph were never visited, so the list says so instead
        // of passing itself off as complete.
        foreach (var (source, name) in probe.Missing)
        {
            missing.Add($"sei/{name}.json from source '{source}'");
        }

        if (missing.Count > 0)
        {
            throw Missing(missing, resolver.BudgetExhausted);
        }

        if (failure is not null)
        {
            // No gap explains the stop and nothing was left unvisited: the
            // cache is whole and the real run will report the same conflict.
            throw failure;
        }
    }

    private static SeikoException Missing(IEnumerable<string> missing, bool incomplete = false)
        => new(
            $"offline: missing cached metadata: {string.Join("; ", missing)}. "
            + (incomplete
                ? "The probe hit its work budget, so the list may be incomplete. "
                : "")
            + "Run the command once online, then retry offline",
            "source");

    // The sources the probe resolves against. It answers like the real list
    // and records, per name, the first source whose cache entry is absent —
    // even when a later source has the name, because the offline run throws
    // at the earlier source before ever asking the later one (first source
    // wins, ADR-0014). Only when no source answers does a placeholder stand
    // in so the walk can continue; a cached 404 is an answer, not a gap.
    private sealed class ProbeSource : ISeiSource, IDisposable
    {
        private readonly List<ISeiSource> _real = [];
        private readonly List<HttpSource> _probes = [];
        private readonly Dictionary<string, int> _owner = new(StringComparer.Ordinal);

        public ProbeSource(SeikoConfig config, string root, string storePath, string rid)
        {
            if (config.Origin.Count > 0)
            {
                _real.Add(new OriginSource(config.Origin, root, rid, storePath));
            }

            foreach (var source in config.Sources)
            {
                if (IsHttp(source))
                {
                    var probe = new HttpSource(source, storePath);
                    _probes.Add(probe);
                    _real.Add(probe);
                }
                else
                {
                    _real.Add(new LocalSource(source, root));
                }
            }
        }

        public SortedSet<(string Source, string Name)> Missing { get; } = [];

        public string Id => "offline-probe";

        public IndexRoot Index => new();

        public SeiCatalog? Catalog => null;

        public SeiIndex? GetSei(string name)
        {
            string? unknown = null;
            for (var i = 0; i < _real.Count; i++)
            {
                var index = _real[i].GetSei(name);
                if (index is not null)
                {
                    if (unknown is not null)
                    {
                        Missing.Add((unknown, name));
                    }

                    _owner[name] = i;
                    return index;
                }

                unknown ??= Unknown(i, name);
            }

            if (unknown is null)
            {
                return null;
            }

            Missing.Add((unknown, name));
            return Placeholder(name);
        }

        // The real cross-source check asks every other source for each
        // resolved name, and the offline run fails on the first cache gap it
        // meets there (ADR-0014, ADR-0028). The check inside Resolve cannot do
        // this because it only ever sees this one aggregate source, so the
        // same reads are replayed here; only metadata gets read, nothing is
        // compared.
        public void CrossCheck(IReadOnlyList<ResolvedSei> resolved)
        {
            foreach (var sei in resolved)
            {
                if (!_owner.TryGetValue(sei.Name, out var owner)
                    || _real[owner] is OriginSource)
                {
                    // Origin entries are exempt from the check (ADR-0044).
                    continue;
                }

                for (var i = 0; i < _real.Count; i++)
                {
                    if (i != owner
                        && _real[i].GetSei(sei.Name) is null
                        && Unknown(i, sei.Name) is { } id)
                    {
                        Missing.Add((id, sei.Name));
                    }
                }
            }
        }

        private string? Unknown(int index, string name)
            => _real[index] is HttpSource http && http.IsCacheUnknown(name) ? http.Id : null;

        public string ResolveArtifact(ArtifactLocation source) => source.Url;

        public Task DownloadAsync(
            string name,
            ArtifactLocation source,
            string directory,
            DownloadSession session,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose()
        {
            foreach (var probe in _probes)
            {
                probe.Dispose();
            }
        }

        // A degenerate index the resolver accepts without judging it (the
        // version is invented, so no range, artifact or relation check may
        // reject it). It carries no dependencies: the missing document's own
        // relations cannot be known from here.
        private static SeiIndex Placeholder(string name)
            => new()
            {
                Name = name,
                Versions =
                [
                    new SeiVersionEntry
                    {
                        ProbeUnknown = true,
                        Version = "1.0.0",
                        Artifacts = [new ArtifactEntry
                        {
                            Source = new ArtifactLocation { Url = "", Sha256 = new string('0', 64), Size = 1 },
                        }],
                    },
                ],
            };
    }

    private static bool HasIndex(SeikoConfig config, string root, string storePath, string sourceId, string name)
    {
        var source = config.Sources.FirstOrDefault(
            item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            return false;
        }

        if (IsHttp(source))
        {
            // A cached 404 does not help a replay: it fails with "not in
            // source" instead of a cache miss, so either way it cannot run.
            var uri = new Uri(new Uri(source.Index), $"sei/{name}.json");
            return File.Exists(MetadataCache.PathFor(storePath, uri.AbsoluteUri));
        }

        // Same resolution the real LocalSource does: relative to the install
        // root, never to the store.
        var index = LocalSource.ResolvePath(source.Index, root);
        return File.Exists(Path.Combine(Path.GetDirectoryName(index) ?? root, "sei", name + ".json"));
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

    private static bool IsHttp(SourceConfig source) => IsHttpUri(source.Index);

    private static bool IsHttpUri(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUri(ISeiSource source) => source is HttpSource;
}
