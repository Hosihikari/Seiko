using NuGet.Versioning;

namespace Seiko;

// Offline mode may only use cached metadata and stored artifacts. Missing
// pieces are collected up front so the user sees one list instead of the
// first failure, and nothing is downloaded implicitly.
internal static class OfflineMetadata
{
    // How many names one run may stub before giving up. Each round stubs at
    // least one new name, so this bounds the loop by the graph size.
    private const int MaxProbeRounds = 512;

    public static void Verify(
        SeikoConfig config,
        string storePath,
        string root,
        IEnumerable<string> initial,
        SeikoLock? lockedGraph = null,
        bool allowPrerelease = false,
        bool allowCritical = false,
        SeikoLock? existing = null,
        string rid = "")
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var httpSources = config.Sources
            .Where(IsHttp)
            .Select(source => (Source: source, IndexUri: new Uri(source.Index)))
            .ToList();

        // A replay is fully described by the lock: check each locked name and
        // stop. Its relations are not re-walked, so no resolver run is needed.
        if (lockedGraph is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var locked in lockedGraph.Sei)
            {
                if (seen.Add(locked.Name)
                    && !string.Equals(locked.Source, OriginPaths.SourceId, StringComparison.Ordinal))
                {
                    if (!HasIndex(config, storePath, locked.Source, locked.Name))
                    {
                        missing.Add($"sei/{locked.Name}.json from source '{locked.Source}'");
                    }
                }
            }

            // The root index is only needed to list names, but a replay that
            // cannot even see the sources cannot be judged either.
            foreach (var (source, indexUri) in httpSources)
            {
                if (!File.Exists(MetadataCache.PathFor(storePath, indexUri.AbsoluteUri)))
                {
                    missing.Add($"index metadata of source '{source.Id}'");
                }
            }

            if (missing.Count > 0)
            {
                throw Missing(missing);
            }

            return;
        }

        // The probe runs the real resolver, so candidate admission, ranges,
        // pre-release hints, yank and critical all come from one place. A name
        // whose metadata is absent is reported and stubbed for the next round
        // instead of aborting the walk.
        var specs = initial.Select(name => new SeiSpec { Name = name }).ToList();
        foreach (var spec in config.Sei)
        {
            var target = specs.FirstOrDefault(
                item => string.Equals(item.Name, spec.Name, StringComparison.Ordinal));
            if (target is not null && target.Version is null)
            {
                target.Version = spec.Version;
            }
        }

        var stubbed = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; ; round++)
        {
            if (round > MaxProbeRounds)
            {
                break;
            }

            foreach (var (source, indexUri) in httpSources)
            {
                if (!File.Exists(MetadataCache.PathFor(storePath, indexUri.AbsoluteUri)))
                {
                    missing.Add($"index metadata of source '{source.Id}'");
                }
            }

            // A missing root index is reported but does not stop the round: the
            // per-name reads below tell us which names are missing, which is the
            // more useful answer.
            var sources = ProbeSources(config, root, storePath, stubbed, specs);
            var resolver = new Resolver(
                sources,
                // The real run's lock, so a yanked version it already holds
                // stays a candidate here too (never the replay branch above).
                existing,
                string.IsNullOrWhiteSpace(rid) ? SeikoClient.DefaultRid() : rid,
                allowPrerelease,
                updateNames: null,
                latestInstallable: false,
                allowCritical);
            try
            {
                resolver.Resolve(specs);
                break;
            }
            catch (SeikoException ex) when (ex.Code is "source" or "resolve")
            {
                // The resolver names the sei it could not find; a failure for
                // any other reason means the cache is whole and the real run
                // will report the same thing.
                var next = NamedIn(ex.Message) ?? FirstUnanswered(specs, sources);
                // Already stubbed means this round added nothing: the failure
                // is not about missing metadata.
                if (next is null || stubbed.Contains(next))
                {
                    throw;
                }

                missing.Add($"sei/{next}.json from source '{SourceOf(config, sources, next)}'");
                stubbed.Add(next);
            }
        }

        if (missing.Count > 0)
        {
            throw Missing(missing);
        }
    }

    private static SeikoException Missing(IEnumerable<string> missing)
        => new(
            $"offline: missing cached metadata: {string.Join("; ", missing)}. "
            + "Run the command once online, then retry offline",
            "source");

    // The sources the probe resolves against: a stub answering every reported
    // name with nothing, then the real ones in declaration order.
    private static List<ISeiSource> ProbeSources(
        SeikoConfig config,
        string root,
        string storePath,
        IReadOnlySet<string> stubbed,
        IReadOnlyList<SeiSpec> specs)
    {
        var sources = new List<ISeiSource>();
        if (config.Origin.Count > 0)
        {
            sources.Add(new OriginSource(
                config.Origin,
                root,
                SeikoClient.DefaultRid(),
                storePath));
        }

        foreach (var source in config.Sources)
        {
            sources.Add(IsHttp(source)
                ? new HttpSource(source, storePath)
                : new LocalSource(source, root));
        }

        // Last: the real sources decide first, the stub only answers the names
        // already reported, so the search can move on.
        if (stubbed.Count > 0)
        {
            sources.Add(new StubSource(stubbed, specs));
        }

        return sources;
    }

    // `sei 'name' not found in any source` — the only resolve failure that is
    // about missing metadata.
    private static string? NamedIn(string message)
    {
        const string Prefix = "sei '";
        var start = message.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Prefix.Length;
        var end = message.IndexOf('\'', start);
        return end > start ? message[start..end] : null;
    }

    // Which source the resolver would have asked: the first one that declares
    // the name in its index, else the first configured one.
    private static string SourceOf(SeikoConfig config, IReadOnlyList<ISeiSource> sources, string name)
        => config.Sources.FirstOrDefault(source =>
                sources.FirstOrDefault(candidate => string.Equals(candidate.Id, source.Id, StringComparison.Ordinal))
                    is { } built && built.Index.Sei?.Contains(name, StringComparer.Ordinal) == true)?.Id
            ?? config.Sources.FirstOrDefault()?.Id
            ?? "unknown";

    private static string? FirstUnanswered(IReadOnlyList<SeiSpec> specs, IReadOnlyList<ISeiSource> sources)
        => specs
            .Select(spec => spec.Name)
            .FirstOrDefault(name => !sources.Any(source => source.GetSei(name) is not null));

    private static bool HasIndex(SeikoConfig config, string storePath, string sourceId, string name)
    {
        var source = config.Sources.FirstOrDefault(
            item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            return false;
        }

        if (IsHttp(source))
        {
            var uri = new Uri(new Uri(source.Index), $"sei/{name}.json");
            return File.Exists(MetadataCache.PathFor(storePath, uri.AbsoluteUri));
        }

        var index = LocalSource.ResolvePath(source.Index, storePath);
        return File.Exists(Path.Combine(Path.GetDirectoryName(index) ?? storePath, "sei", name + ".json"));
    }

    // Answers only the names an earlier round reported, with an empty index, so
    // the search walks past them instead of failing on the same name again.
    private sealed class StubSource : ISeiSource
    {
        private readonly IReadOnlySet<string> _names;

        private readonly IReadOnlyList<SeiSpec> _specs;

        public StubSource(IReadOnlySet<string> names, IReadOnlyList<SeiSpec> specs)
        {
            _names = names;
            _specs = specs;
        }

        public string Id => "offline-stub";

        public IndexRoot Index => new();

        public SeiCatalog? Catalog => null;

        // One synthesized version so the search can move past the name; it is
        // only there to satisfy candidate admission, never to be installed.
        public SeiIndex? GetSei(string name)
            => _names.Contains(name)
                ? new SeiIndex
                {
                    Name = name,
                    Versions =
                    [
                        new SeiVersionEntry
                        {
                            Version = VersionFor(name),
                            Artifacts = [new ArtifactEntry
                            {
                                Source = new ArtifactLocation { Url = "", Sha256 = new string('0', 64), Size = 1 },
                            }],
                        },
                    ],
                }
                : null;

        // A version inside the declared range when there is one, so the stub
        // passes the same admission the real entry would; the value is never
        // installed.
        private string VersionFor(string name)
            => _specs.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal))
                ?.Version is { } text
                && NuGet.Versioning.VersionRange.TryParse(text, out var range)
                && range.MinVersion is { } min
                    ? min.ToNormalizedString()
                    : "1.0.0";

        public string ResolveArtifact(ArtifactLocation source) => source.Url;

        public Task DownloadAsync(
            string name,
            ArtifactLocation source,
            string directory,
            DownloadSession session,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
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
