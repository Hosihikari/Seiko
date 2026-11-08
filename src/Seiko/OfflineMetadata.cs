using NuGet.Versioning;

namespace Seiko;

// Offline mode may only use cached metadata and stored artifacts. Missing
// pieces are collected up front so the user sees one list instead of the
// first failure, and nothing is downloaded implicitly.
internal static class OfflineMetadata
{
    // The gaps found, and a failure met on the way ("missing" and "broken" are
    // both set when both happened). The caller throws once the whole
    // operation has been checked, so two pre-checks cannot hide each other's
    // gaps behind the first exception.
    public sealed record VerifyResult(SortedSet<string> Missing, SeikoException? Failure, bool Incomplete)
    {
        public void ThrowIfAny()
        {
            var incomplete = Incomplete;
            if (Failure is not null && Missing.Count > 0)
            {
                throw Combined(Failure, Missing, incomplete);
            }

            if (Missing.Count > 0)
            {
                throw MissingError(Missing, incomplete);
            }

            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    public static VerifyResult Verify(
        SeikoConfig config,
        string storePath,
        string root,
        IReadOnlyList<SeiSpec> specs,
        SeikoLock? frozenGraph = null,
        bool allowPrerelease = false,
        bool allowCritical = false,
        SeikoLock? existing = null,
        string rid = "",
        IReadOnlySet<string>? updateNames = null,
        bool latestInstallable = false)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        // `--frozen` is the one mode that never resolves: it reads the locked
        // source of every entry and then every other source's index for the
        // same names (the cross-source check), then compares. So the check is
        // a replay of exactly those reads, with the same readability rule as
        // the real cache read and with "a cached 404" told apart from "no
        // cached answer". `--locked` resolves normally and takes the path
        // below with the lock passed in.
        if (frozenGraph is not null)
        {
            // The locked source answering "not there" is a lock failure, not a
            // cache gap: refetching may well answer 404 again. It is kept
            // apart so the failure keeps its own code, and merged with real
            // gaps at the end.
            var absent = new SortedSet<string>(StringComparer.Ordinal);
            var dead = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var locked in frozenGraph.Sei)
            {
                if (!seen.Add(locked.Name)
                    || string.Equals(locked.Source, OriginPaths.SourceId, StringComparison.Ordinal))
                {
                    // Origin entries are read from disk, not from the cache.
                    continue;
                }

                CheckLockedRead(config, root, storePath, locked.Source, locked.Name, missing, absent, dead,
                    crossCheck: false);
                foreach (var source in config.Sources)
                {
                    if (!string.Equals(source.Id, locked.Source, StringComparison.Ordinal))
                    {
                        CheckLockedRead(config, root, storePath, source.Id, locked.Name, missing, absent, dead,
                            crossCheck: true);
                    }
                }
            }

            if (dead.Count > 0)
            {
                // The source itself is unusable; that is the run's failure
                // code, not a lock verdict.
                return new VerifyResult(missing, new SeikoException(string.Join("; ", dead), "source"), false);
            }

            if (absent.Count > 0)
            {
                return new VerifyResult(
                    missing,
                    new SeikoException($"offline: the lock cannot be replayed: {string.Join("; ", absent)}", "lock"),
                    false);
            }

            return new VerifyResult(missing, null, false);
        }

        // The probe runs the real resolver with the planned declarations and
        // options, so candidate admission, ranges, pre-release hints, yank and
        // critical all come from one place. The source records every read
        // whose answer is missing, and the run collects every graph it
        // reaches, so one pass finds the gaps on all of them.
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
            allowCritical,
            probe.OnSolution,
            collectAll: true);
        SeikoException? failure = null;
        try
        {
            resolver.Resolve(specs);
        }
        catch (SeikoException ex) when (ex.Code is "source" or "resolve" or "checksum")
        {
            failure = ex;
        }

        // The gaps beat the failure: once the cache is whole the real run
        // reports the same thing. Even a checksum failure on a stale origin
        // file must not hide a gap that was already read.
        foreach (var (source, name) in probe.Missing)
        {
            missing.Add($"sei/{name}.json from source '{source}'");
        }



        return new VerifyResult(missing, probe.CheckFailure ?? failure, resolver.Incomplete);
    }

    // A failure met after gaps were read carries them along: the gaps are
    // what to refill, the failure is why the walk could not go on. The code
    // stays the failure's, since it is still the reason the run stops.
    private static SeikoException Combined(
        SeikoException failure,
        IEnumerable<string> gaps,
        bool incomplete)
        => new(
            $"{failure.Message} (offline: missing cached metadata: {string.Join("; ", gaps)}"
            + (incomplete ? "; the probe hit a search limit, so the list may be incomplete" : "")
            + ")",
            failure.Code ?? "source")
        {
            // The hint belongs to the failure and still applies.
            Hint = failure.Hint,
            HintKey = failure.HintKey,
        };

    private static SeikoException MissingError(IEnumerable<string> missing, bool incomplete)
        => new(
            $"offline: missing cached metadata: {string.Join("; ", missing)}. "
            + (incomplete
                ? "The probe hit a search limit, so the list may be incomplete. "
                : "")
            + "Run the command once online, then retry offline",
            "source");

    // The sources the probe resolves against, plus the gap log. Every read
    // goes to the real list in order; a source whose cache has no answer is
    // reported at the moment it is read, and every such source is reported —
    // the real offline run stops at the first one, so a later one is a second
    // retry it would need. A cached 404 is an answer and reports nothing.
    private sealed class ProbeSource : ISeiSource, IDisposable
    {
        private readonly List<ISeiSource> _real = [];
        private readonly List<HttpSource> _probes = [];
        private readonly Dictionary<string, int> _owner = new(StringComparer.Ordinal);

        public ProbeSource(SeikoConfig config, string root, string storePath, string rid)
        {
            try
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
                        // A configured local index that is gone fails here; the
                        // probes built so far still have a session each.
                        _real.Add(new LocalSource(source, root));
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public SortedSet<(string Source, string Name)> Missing { get; } = [];

        public string Id => "offline-probe";

        public IndexRoot Index => new();

        public SeiCatalog? Catalog => null;

        public SeiIndex? GetSei(string name)
        {
            var unknownBefore = false;
            for (var i = 0; i < _real.Count; i++)
            {
                var read = Read(i, name);
                if (read is not null)
                {
                    _owner.TryAdd(name, i);
                    // The real run still stops at the earlier source that
                    // could not be read: whatever it holds wins, and it is
                    // not necessarily this entry.
                    read.ProbeShadowed = unknownBefore;
                    return read;
                }

                unknownBefore |= _real[i] is HttpSource http && http.IsCacheUnknown(name);
            }

            if (!unknownBefore)
            {
                return null;
            }

            // Somebody read this name and could not fill it; the walk must go
            // on past it to find the rest.
            return Placeholder(name);
        }

        // The graph the rest of the run would cross-check. The check inside
        // Resolve only ever sees this one aggregate source, so the other
        // sources are read here exactly the way CrossSourceCheck reads them;
        // whatever they lack is a gap, whether or not the check itself would
        // then fail. Only a trusted framework-level handler wants this.
        public void OnSolution(IReadOnlyList<ResolvedSei> resolved)
        {
            try
            {
                // The real check runs here too, so it stops at a digest
                // mismatch just like the run: sources after that one are not
                // read and stay out of the gap list. Whatever it throws is
                // already the answer — the resolver wraps the call anyway.
                // Only names an index source actually owns reach the check;
                // an origin entry is exempt (ADR-0044).
                var owned = resolved
                    .Where(sei => _owner.TryGetValue(sei.Name, out var index) && _real[index] is not OriginSource)
                    .ToList();
                if (owned.Count == 0)
                {
                    return;
                }

                CrossSourceCheck.Verify(_real, owned, "checksum", (source, name) =>
                {
                    var index = _real.IndexOf(source);
                    if (index >= 0 && index != _owner.GetValueOrDefault(name, -1))
                    {
                        Read(index, name);
                    }
                });
            }
            catch (SeikoException ex) when (ex.Code == "checksum")
            {
                _checkFailure = ex;
            }
            catch (SeikoException)
            {
                // Anything else (a source's own failure) is the resolver's to
                // report; the gaps already collected still stand.
            }
        }

        // A cross-source failure already proven by cached data. It is the
        // run's stopping point, so it takes precedence over gaps found later.
        public SeikoException? CheckFailure => _checkFailure;

        private SeikoException? _checkFailure;

        private SeiIndex? Read(int index, string name)
        {
            var read = _real[index].GetSei(name);
            if (read is null && _real[index] is HttpSource http && http.IsCacheUnknown(name))
            {
                Missing.Add((http.Id, name));
            }

            return read;
        }

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

    // One read the replay will make, with the answer kept in the same shapes
    // the real source uses: a recorded 404 is "known absent" and a missing or
    // unreadable cache file is "unknown". Only a read the run actually makes
    // counts as a gap. A local source reads its directory, so a missing file
    // there is simply "this source does not have the name" — not a gap.
    private static void CheckLockedRead(
        SeikoConfig config,
        string root,
        string storePath,
        string sourceId,
        string name,
        SortedSet<string> missing,
        SortedSet<string> absent,
        SortedSet<string> dead,
        bool crossCheck)
    {
        var source = config.Sources.FirstOrDefault(
            item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            absent.Add($"source '{sourceId}' for {name} is not configured");
            return;
        }

        // The source has to open the way the real constructor would before
        // its answer about the name means anything.
        if (OpenProblem(source, root, storePath) is { } problem)
        {
            dead.Add(problem);
            return;
        }

        if (!IsHttp(source))
        {
            var index = LocalSource.ResolvePath(source.Index, root);
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(index) ?? root, "sei", name + ".json"))
                && !crossCheck)
            {
                // An open local source answers from its directory: a missing
                // file is "this source does not have the name".
                absent.Add($"sei/{name}.json from source '{sourceId}'");
            }

            return;
        }

        var path = MetadataCache.PathFor(
            storePath,
            new Uri(new Uri(source.Index), $"sei/{name}.json").AbsoluteUri);
        if (MetadataCache.IsMissing(path))
        {
            // Known absent upstream: the cross-source pass just skips it, but
            // a locked source that answered 404 makes the replay fail.
            if (!crossCheck)
            {
                absent.Add($"sei/{name}.json from source '{sourceId}' (recorded as absent)");
            }

            return;
        }

        if (!File.Exists(path))
        {
            missing.Add($"sei/{name}.json from source '{sourceId}'");
            return;
        }

        if (MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex) is null)
        {
            missing.Add($"sei/{name}.json from source '{sourceId}' (unreadable)");
        }
    }

    // Why the real constructor of this source would fail, if it would. A
    // local index has to exist and parse with the current schema; a cached
    // root has to parse if it is there at all. Null means the source opens.
    private static string? OpenProblem(SourceConfig source, string root, string storePath)
    {
        if (IsHttp(source))
        {
            var indexPath = MetadataCache.PathFor(
                storePath,
                new Uri(new Uri(source.Index), "index.json").AbsoluteUri);
            return File.Exists(indexPath)
                && MetadataCache.TryRead(indexPath, SeikoJsonContext.Default.IndexRoot) is null
                    ? $"source '{source.Id}': invalid index"
                    : null;
        }

        var path = LocalSource.ResolvePath(source.Index, root);
        if (!File.Exists(path))
        {
            return $"source '{source.Id}': index not found: {path}";
        }

        var index = MetadataCache.TryRead(path, SeikoJsonContext.Default.IndexRoot);
        return index is null
            ? $"source '{source.Id}': invalid index"
            : index.SchemaVersion != IndexRoot.CurrentSchemaVersion
                ? $"source '{source.Id}': unsupported index schemaVersion {index.SchemaVersion}"
                : null;
    }

    // The bare-token disambiguation asks each index source for one name in
    // order and stops at the first hit, exactly like `info`. Its reads happen
    // before the real run too, so they get the same treatment.
    // The answer is written into the caller's list, so a resolution pre-check
    // and this walk fail together instead of one hiding the other. A name
    // keeps walking while the read source is unknown or does not have it; an
    // unknown source is not a hit and may well defer to a later one.
    public static void VerifyNames(
        SortedSet<string> missing,
        SeikoConfig config,
        string storePath,
        string root,
        IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            foreach (var source in config.Sources)
            {
                if (!IsHttp(source))
                {
                    var index = LocalSource.ResolvePath(source.Index, root);
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(index) ?? root, "sei", name + ".json")))
                    {
                        break;
                    }

                    continue;
                }

                var path = MetadataCache.PathFor(
                    storePath,
                    new Uri(new Uri(source.Index), $"sei/{name}.json").AbsoluteUri);
                if (MetadataCache.IsMissing(path))
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    missing.Add($"sei/{name}.json from source '{source.Id}'");
                    continue;
                }

                if (MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex) is null)
                {
                    // A corrupt file answers nothing, so keep looking: an
                    // earlier source may still answer once it is refilled.
                    missing.Add($"sei/{name}.json from source '{source.Id}' (unreadable)");
                    continue;
                }

                break;
            }
        }
    }

    // `info` walks the sources in order and stops at the first one that has
    // the name; it neither resolves dependencies nor cross-checks. So only
    // that first answering source has to be readable — a later one may be
    // cold without it mattering.
    public static void VerifyInfo(SeikoConfig config, string storePath, string root, string name)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        // The real source list starts with the origin declarations, and the
        // command stops at the first answer; an origin entry answers first.
        var origin = config.Origin.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (origin is not null)
        {
            return;
        }

        foreach (var source in config.Sources)
        {
            if (!IsHttp(source))
            {
                var index = LocalSource.ResolvePath(source.Index, root);
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(index) ?? root, "sei", name + ".json")))
                {
                    continue;
                }

                return;
            }

            var path = MetadataCache.PathFor(
                storePath,
                new Uri(new Uri(source.Index), $"sei/{name}.json").AbsoluteUri);
            if (MetadataCache.IsMissing(path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                missing.Add($"sei/{name}.json from source '{source.Id}'");
                break;
            }

            if (MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex) is null)
            {
                missing.Add($"sei/{name}.json from source '{source.Id}' (unreadable)");
            }

            return;
        }

        if (missing.Count > 0)
        {
            throw MissingError(missing, incomplete: false);
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
