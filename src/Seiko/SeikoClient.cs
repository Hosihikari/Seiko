using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NuGet.Versioning;

namespace Seiko;

public sealed class SyncResult
{
    public required SeikoLock Lock { get; init; }
    public required int FilesDeployed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int SeiInstalled => Lock.Sei.Count;
    public PreviewResult? Preview { get; init; }
    public IReadOnlyList<PreviewDiagnostic> Diagnostics { get; init; } = [];
    public int ExternalItems { get; init; }
    public IReadOnlyList<string> ExternalHosts { get; init; } = [];
}

public sealed class RollbackResult
{
    public required int Generation { get; init; }
    public required int SeiInstalled { get; init; }
    public required int FilesDeployed { get; init; }
}

public sealed record OutdatedSei(
    string Name,
    string Current,
    string? LatestInRange,
    string Latest,
    string? LatestInstallable,
    IReadOnlyList<OutdatedBlocker> Blockers);

public sealed record OutdatedBlocker(
    string Relation,
    string Target,
    string? Range,
    string Source);

public sealed record CacheStatusInfo(
    int Artifacts,
    long Bytes,
    int PartialItems,
    int StagingItems,
    long StagingBytes);

public sealed record CacheFinding(string Severity, string Code, string Message, string? Path);

public sealed record CacheCleanResult(int Removed, long Bytes);

public sealed class SeikoClient
{
    internal const string PendingTransactionWarning =
        "an interrupted deployment transaction exists; run `sk sync` without --dry-run to recover it";

    public SyncResult Sync(
        string root,
        string? storeRoot = null,
        bool allowPrerelease = false,
        SyncMode mode = SyncMode.Normal,
        int jobs = DownloadSession.DefaultJobs,
        string? proxy = null,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? updateNames = null,
        bool latestInstallable = false,
        bool allowCritical = false,
        bool dryRun = false,
        bool adopt = false,
        bool force = false,
        bool offline = false)
    {
        root = Path.GetFullPath(root);
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == SyncMode.Frozen && latestInstallable)
        {
            throw new SeikoException("--frozen and --latest-installable are mutually exclusive", "usage");
        }

        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        return SyncLocked(
            root,
            storePath,
            allowPrerelease,
            mode,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames,
            latestInstallable,
            allowCritical,
            dryRun: dryRun,
            adopt: adopt,
            force: force,
            offline: offline);
    }

    private SyncResult SyncLocked(
        string root,
        string storePath,
        bool allowPrerelease,
        SyncMode mode,
        int jobs,
        string? proxy,
        IProgress<SyncProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? updateNames,
        bool latestInstallable,
        bool allowCritical,
        DeploymentTransaction? transaction = null,
        bool dryRun = false,
        IReadOnlyList<SeiSpec>? specsOverride = null,
        bool adopt = false,
        bool force = false,
        bool offline = false)
    {
        var pendingTransaction = false;
        if (transaction is null)
        {
            var recoveryDirectory = Path.Combine(root, ".seiko");
            if (dryRun)
            {
                // Recovery writes config/lock/deployment; a preview must not
                // (ADR-0029). Detect it and let the real sync recover.
                pendingTransaction = File.Exists(Path.Combine(recoveryDirectory, "transaction.json"));
            }
            else
            {
                Deployer.Recover(root, recoveryDirectory);
            }
        }

        if (offline && mode == SyncMode.Normal && !dryRun)
        {
            throw new SeikoException(
                "offline: sync needs --locked or --frozen; add/remove/update require a network update of cached metadata",
                "source");
        }

        var config = ConfigLoader.Load(root);
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var session = new DownloadSession(jobs, downloadCts.Token)
        {
            Progress = progress,
            Log = log,
        };
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
        var lockPath = Path.Combine(root, "seiko.lock.json");
        var existingLock = LockFile.Read(lockPath);
        var specs = specsOverride ?? config.Sei;

        if (offline)
        {
            // Collect every missing cached entry before constructing sources,
            // which would otherwise fail on the first one. A replay only needs
            // the locked graph, not every historical relation target.
            OfflineMetadata.Verify(
                config,
                storePath,
                config.Sei.Select(spec => spec.Name),
                lockedGraph: mode == SyncMode.Normal ? null : existingLock);
        }

        var sources = LoadSources(config, root, proxy, session, storePath, offline);
        using var sourceScope = new SourceScope(sources);

        IReadOnlyList<ResolvedSei> resolved;
        SeikoLock lockFile;
        IReadOnlyList<string> warnings;

        if (mode == SyncMode.Frozen)
        {
            if (existingLock is null)
            {
                throw new SeikoException("--frozen requires seiko.lock.json", "lock");
            }

            if (!string.Equals(existingLock.Target.Rid, rid, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"--frozen: lock targets rid '{existingLock.Target.Rid}', but the config selects '{rid}'; generate a lock for this platform first",
                    "lock");
            }

            resolved = LockReplay.Resolve(sources, existingLock, specs);
            lockFile = existingLock;
            warnings = Retirement.Warnings(resolved);
        }
        else
        {
            var resolver = new Resolver(
                sources,
                existingLock,
                rid,
                allowPrerelease,
                updateNames,
                latestInstallable,
                allowCritical);
            resolved = resolver.Resolve(specs);
            warnings = resolver.Warnings();
            lockFile = LockFile.FromResolution(rid, sources, resolved);

            if (mode == SyncMode.Locked)
            {
                if (existingLock is null)
                {
                    throw new SeikoException("--locked requires seiko.lock.json", "lock");
                }

                var difference = LockReplay.Compare(existingLock, lockFile);
                if (difference is not null)
                {
                    throw new SeikoException($"--locked: lock is out of date ({difference})", "lock");
                }
            }
        }

        var totalBytes = resolved.Sum(sei => Math.Max(0L, sei.Artifact.Source.Size));
        session.SeiTotal = resolved.Count;
        session.TotalBytes = totalBytes;
        progress?.Report(new SyncProgress(SyncPhase.Resolved, null, 0, totalBytes, 0, resolved.Count));

        var store = new Store(storePath);
        if (offline)
        {
            OfflineMetadata.EnsureArtifacts(store, resolved);
        }

        if (dryRun)
        {
            var previewWarnings = pendingTransaction
                ? warnings.Concat([PendingTransactionWarning]).ToList()
                : warnings;
            return new SyncResult
            {
                Lock = lockFile,
                FilesDeployed = 0,
                Warnings = previewWarnings,
                Preview = Preview(existingLock, lockFile, resolved, mode, previewWarnings),
            };
        }

        // Report conflicts before downloading anything; a later deploy still
        // re-checks against the on-disk state.
        var stored = DownloadAll(store, resolved, session, downloadCts);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new SyncProgress(
            SyncPhase.Deploying,
            null,
            totalBytes,
            totalBytes,
            resolved.Count,
            resolved.Count));
        var deployedSei = stored
            .Select(item => new DeployedSei(
                item.Sei.Name,
                item.Sei.Version,
                item.Stored.Directory,
                item.Stored.Manifest,
                item.Stored.Digests))
            .ToList();
        var stateDirectory = Path.Combine(root, ".seiko");
        var deployment = transaction is null
            ? Deployer.Deploy(
                root,
                deployedSei,
                stateDirectory,
                lockFile,
                writeLock: mode == SyncMode.Normal,
                adopt: adopt,
                force: force)
            : Deployer.DeployInTransaction(
                root,
                deployedSei,
                stateDirectory,
                lockFile,
                transaction,
                writeLock: mode == SyncMode.Normal,
                adopt: adopt,
                force: force);

        var described = new List<PreviewDiagnostic>();
        foreach (var sei in resolved.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            Describe(sei, described);
        }

        // The real sync reports the materialized manifests, not the index's
        // derived summary: the manifest is the authority (ADR-0037). A summary
        // that disagrees is surfaced as a warning, not a block.
        var resultWarnings = warnings;
        var externalWarnings = new List<string>();
        foreach (var (sei, storedSei) in stored)
        {
            var entry = IndexArtifactFor(sei);
            if (entry.External is { } declared
                && !ExternalSummaryMatches(declared, storedSei.Manifest.Artifact))
            {
                externalWarnings.Add(
                    $"{sei.Name}: index external summary does not match sei.json; using the manifest");
            }
        }

        if (externalWarnings.Count > 0)
        {
            resultWarnings = warnings.Concat(externalWarnings).ToList();
        }

        var external = SummarizeExternal(stored.Select(item => item.Stored.Manifest));
        return new SyncResult
        {
            Lock = lockFile,
            FilesDeployed = deployment.FileCount,
            Warnings = resultWarnings,
            Diagnostics = described,
            ExternalItems = external.Items,
            ExternalHosts = external.Hosts,
        };
    }

    private static (int Items, IReadOnlyList<string> Hosts) SummarizeExternal(
        IEnumerable<SeiManifest> manifests)
    {
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = 0;
        foreach (var manifest in manifests)
        {
            items += ExternalFiles.Count(manifest.Artifact);
            foreach (var host in ExternalFiles.Hosts(manifest.Artifact))
            {
                hosts.Add(host);
            }
        }

        return (items, hosts.ToList());
    }

    private static void Describe(ResolvedSei sei, List<PreviewDiagnostic> diagnostics)
    {
        var rid = sei.Artifact.Rid ?? "*";
        diagnostics.Add(new PreviewDiagnostic(
            sei.Name,
            "artifact",
            $"selected {sei.Artifact.Source.Sha256} from source '{sei.Source.Id}' (rid {rid})"));

        foreach (var dependency in sei.Entry.Dependencies)
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "dependency",
                $"requires {dependency.Name} {dependency.Version ?? "*"} (version scope)"));
        }

        foreach (var prerequisite in sei.Entry.Prerequisites)
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "prerequisite",
                $"needs {prerequisite.Name} {prerequisite.Version ?? "*"} (version scope)"));
        }

        foreach (var peer in sei.Entry.OptionalPeers)
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "optional-peer",
                $"constrains {peer.Name} {peer.Version ?? "*"} when present (version scope)"));
        }

        foreach (var dependency in sei.Artifact.Dependencies ?? [])
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "artifact-dependency",
                $"requires {dependency.Name} {dependency.Version ?? "*"} (artifact scope)"));
        }

        foreach (var prerequisite in sei.Artifact.Prerequisites ?? [])
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "artifact-prerequisite",
                $"needs {prerequisite.Name} {prerequisite.Version ?? "*"} to match this artifact"));
        }

        foreach (var peer in sei.Artifact.OptionalPeers ?? [])
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "artifact-optional-peer",
                $"constrains {peer.Name} {peer.Version ?? "*"} when present (artifact scope)"));
        }

        foreach (var conflict in sei.Entry.Conflicts)
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "conflict",
                $"excludes {conflict.Name} {conflict.Version ?? "*"}"));
        }

        foreach (var conflict in sei.Artifact.Conflicts ?? [])
        {
            diagnostics.Add(new PreviewDiagnostic(
                sei.Name,
                "artifact-conflict",
                $"excludes {conflict.Name} {conflict.Version ?? "*"} for this artifact"));
        }
    }

    private static PreviewResult Preview(
        SeikoLock? existing,
        SeikoLock proposed,
        IReadOnlyList<ResolvedSei> resolved,
        SyncMode mode,
        IReadOnlyList<string> warnings)
    {
        var previous = existing?.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal)
            ?? new Dictionary<string, LockedSei>(StringComparer.Ordinal);
        var current = proposed.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        var changes = new List<PlannedChange>();

        foreach (var sei in resolved.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            previous.TryGetValue(sei.Name, out var old);
            var action = old is null
                ? "add"
                : old.Version == sei.Version
                    && string.Equals(old.Artifact.Sha256, sei.Artifact.Source.Sha256, StringComparison.OrdinalIgnoreCase)
                        ? "keep"
                        : "update";
            if (action == "keep" && mode != SyncMode.Normal)
            {
                action = "replay";
            }

            changes.Add(new PlannedChange(
                action,
                sei.Name,
                old?.Version,
                sei.Version,
                sei.Source.Id,
                sei.Artifact.Source.Sha256));
        }

        foreach (var removed in previous.Keys
            .Where(name => !current.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            changes.Add(new PlannedChange("remove", removed, previous[removed].Version, null, null, null));
        }

        var blocked = new List<PreviewBlock>();
        foreach (var sei in resolved.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            var index = sei.Source.GetSei(sei.Name);
            if (index is null)
            {
                continue;
            }

            var highest = index.Versions
                .Select(entry => (Entry: entry, Version: NuGetVersion.TryParse(entry.Version, out var parsed) ? parsed : null))
                .Where(item => item.Version is not null && !item.Entry.Yanked)
                .OrderByDescending(item => item.Version)
                .FirstOrDefault();
            if (highest.Version is null || highest.Version <= sei.ParsedVersion)
            {
                continue;
            }

            blocked.Add(new PreviewBlock(
                "newer-version",
                $"{sei.Name} {highest.Version.ToNormalizedString()} is available; the graph keeps {sei.Version}"));
        }

        var diagnostics = new List<PreviewDiagnostic>();
        foreach (var sei in resolved.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            Describe(sei, diagnostics);
        }

        // External payload comes from index summaries when present, or from
        // the entry's own declarations; nothing is downloaded for the plan.
        // With neither, the index cannot tell us and zero would be a lie.
        var externalHosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var externalUnknown = new List<string>();
        var externalItems = 0;
        var externalBytes = 0L;
        foreach (var sei in resolved)
        {
            var artifact = IndexArtifactFor(sei);
            // Declarations are the source of truth; the summary is only a
            // stand-in for indexes that never mirrored them (ADR-0037).
            if (artifact.ExternalFiles is not null || artifact.ExternalArchives is not null)
            {
                var manifest = new ArtifactManifest
                {
                    ExternalFiles = artifact.ExternalFiles,
                    ExternalArchives = artifact.ExternalArchives,
                };
                externalItems += ExternalFiles.Count(manifest);
                externalBytes += (artifact.ExternalFiles ?? []).Sum(file => file.Size)
                    + (artifact.ExternalArchives ?? []).Sum(archive => archive.Size);
                foreach (var host in ExternalFiles.Hosts(manifest))
                {
                    externalHosts.Add(host);
                }

                continue;
            }

            if (artifact.External is { } summary)
            {
                externalItems += summary.Files + summary.Archives;
                externalBytes += summary.Size;
                foreach (var host in summary.Hosts)
                {
                    externalHosts.Add(host);
                }

                continue;
            }

            externalUnknown.Add(sei.Name);
        }

        return new PreviewResult
        {
            Changes = changes,
            Blocked = blocked,
            Diagnostics = diagnostics,
            Warnings = warnings,
            ExternalItems = externalItems,
            ExternalBytes = externalBytes,
            ExternalHosts = externalHosts.ToList(),
            ExternalUnknown = externalUnknown,
        };
    }

    private static ArtifactEntry IndexArtifactFor(ResolvedSei sei)
        => sei.Entry.Artifacts?
            .FirstOrDefault(artifact => artifact is not null
                && string.Equals(
                    artifact.Source?.Sha256,
                    sei.Artifact.Source.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            ?? sei.Artifact;

    private static bool ExternalSummaryMatches(ExternalSummary declared, ArtifactManifest? manifest)
        => ExternalFiles.SummaryMatches(declared, manifest);

    private static List<(ResolvedSei Sei, StoredSei Stored)> DownloadAll(
        Store store,
        IReadOnlyList<ResolvedSei> resolved,
        DownloadSession session,
        CancellationTokenSource downloadCts)
    {
        Exception? firstError = null;
        // Starting every EnsureAsync at once opens one artifact lock per
        // package before any HTTP slot is taken, so the handle peak would grow
        // with the graph regardless of --jobs (ADR-0017 bounds requests, not
        // handles). Admit only as many ensures as there are jobs; the rest
        // enter as earlier ones finish.
        var bound = Math.Max(1, session.Jobs);
        using var slots = new SemaphoreSlim(bound, bound);
        var tasks = resolved
            .Select(async sei =>
            {
                await slots.WaitAsync(downloadCts.Token).ConfigureAwait(false);
                try
                {
                    return (Sei: sei, Stored: await store.EnsureCountedAsync(sei, session, downloadCts.Token)
                        .ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    downloadCts.Cancel();
                    throw;
                }
                finally
                {
                    slots.Release();
                }
            })
            .ToArray();

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch
        {
            downloadCts.Cancel();
            try
            {
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            catch
            {
                // Preserve the first failure below.
            }

            if (firstError is not null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }

            throw;
        }

        return tasks.Select(task => task.Result).ToList();
    }

    public SyncResult Add(
        string root,
        string specification,
        string? storeRoot = null,
        bool allowPrerelease = false,
        int jobs = DownloadSession.DefaultJobs,
        string? proxy = null,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool latestInstallable = false,
        bool allowCritical = false,
        bool dryRun = false,
        bool adopt = false,
        bool force = false,
        bool offline = false)
        => AddMany(
            root,
            [specification],
            remove: null,
            storeRoot,
            allowPrerelease,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            latestInstallable,
            allowCritical,
            dryRun,
            adopt,
            force,
            offline);

    public SyncResult AddMany(
        string root,
        IReadOnlyList<string> specifications,
        IReadOnlyList<string>? remove = null,
        string? storeRoot = null,
        bool allowPrerelease = false,
        int jobs = DownloadSession.DefaultJobs,
        string? proxy = null,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool latestInstallable = false,
        bool allowCritical = false,
        bool dryRun = false,
        bool adopt = false,
        bool force = false,
        bool offline = false)
    {
        root = Path.GetFullPath(root);
        var additions = specifications
            .Select(ParseAddSpecification)
            .ToList();
        var removals = (remove ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (additions.Count == 0 && removals.Count == 0)
        {
            throw new SeikoException("add requires at least one sei or --remove", "usage");
        }

        RequireOnline(offline, dryRun, "add");

        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        RecoverUnlessPreview(root, dryRun);

        var config = ConfigLoader.Load(root);
        var addNames = additions.Select(addition => addition.Name).ToHashSet(StringComparer.Ordinal);
        var removedNames = removals.ToHashSet(StringComparer.Ordinal);

        var defaultRangeTargets = new List<SeiSpec>();
        foreach (var addition in additions)
        {
            var existing = config.Sei.FirstOrDefault(spec => string.Equals(spec.Name, addition.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                var spec = new SeiSpec { Name = addition.Name, Version = addition.Version };
                config.Sei.Add(spec);
                if (addition.Version is null)
                {
                    defaultRangeTargets.Add(spec);
                }
            }
            else if (addition.Version is not null)
            {
                existing.Version = addition.Version;
            }
        }

        foreach (var name in removedNames)
        {
            if (addNames.Contains(name))
            {
                throw new SeikoException($"cannot add and remove {name} in the same operation", "usage");
            }

            if (config.Sei.RemoveAll(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)) == 0)
            {
                throw new SeikoException($"{name} is not a direct dependency", "resolve");
            }
        }

        var existingLock = LockFile.Read(Path.Combine(root, "seiko.lock.json"));
        if (defaultRangeTargets.Count > 0 || (existingLock is not null && removedNames.Count > 0))
        {
            using var session = new DownloadSession(jobs, cancellationToken);
            var sources = LoadSources(config, root, proxy, session, storePath, offline);
            using var sourceScope = new SourceScope(sources);
            var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
            var resolved = new Resolver(
                sources,
                existingLock,
                rid,
                allowPrerelease,
                updateNames: null,
                latestInstallable,
                allowCritical).Resolve(config.Sei);

            foreach (var target in defaultRangeTargets)
            {
                var selected = resolved.FirstOrDefault(sei => string.Equals(sei.Name, target.Name, StringComparison.Ordinal))
                    ?? throw new SeikoException($"no resolved version for {target.Name}", "resolve");
                target.Version = DefaultAddRange(selected.ParsedVersion);
            }

            if (existingLock is not null && removedNames.Count > 0)
            {
                EnsureRemoveKeepsSelections(existingLock, resolved, removedNames, addNames);
            }
        }

        if (dryRun)
        {
            return PreviewWithoutWriting(
                root,
                storePath,
                config.Sei,
                allowPrerelease,
                mode: SyncMode.Normal,
                jobs,
                proxy,
                progress,
                log,
                cancellationToken,
                updateNames: null,
                latestInstallable,
                allowCritical,
                adopt,
                force,
                offline);
        }

        return WriteSpecsAndSync(
            root,
            config.Sei,
            transaction => SyncLocked(
                root,
                storePath,
                allowPrerelease,
                SyncMode.Normal,
                jobs,
                proxy,
                progress,
                log,
                cancellationToken,
                updateNames: null,
                latestInstallable,
                allowCritical,
                transaction,
                adopt: adopt,
                force: force));
    }

    // A preview must not recover a pending transaction (ADR-0029); a real
    // mutation must, before it reads the config (ADR-0019). Every mutation
    // entry uses this so a change to the policy stays in one place.
    private static void RecoverUnlessPreview(string root, bool dryRun)
    {
        if (!dryRun)
        {
            Deployer.Recover(root, Path.Combine(root, ".seiko"));
        }
    }

    private SyncResult PreviewWithoutWriting(
        string root,
        string storePath,
        IReadOnlyList<SeiSpec> specs,
        bool allowPrerelease,
        SyncMode mode,
        int jobs,
        string? proxy,
        IProgress<SyncProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? updateNames,
        bool latestInstallable,
        bool allowCritical,
        bool adopt = false,
        bool force = false,
        bool offline = false)
        => SyncLocked(
            root,
            storePath,
            allowPrerelease,
            mode,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames,
            latestInstallable,
            allowCritical,
            transaction: null,
            dryRun: true,
            specs,
            adopt,
            force,
            offline);

    private static SyncResult WriteSpecsAndSync(
        string root,
        IReadOnlyList<SeiSpec> specs,
        Func<DeploymentTransaction, SyncResult> sync,
        Action<DeploymentTransaction>? afterConfigWrite = null)
    {
        var stateDirectory = Path.Combine(root, ".seiko");
        DeploymentTransaction? transaction = null;
        try
        {
            transaction = DeploymentTransaction.Begin(root, stateDirectory);
            transaction.WillChange("seiko.conf");
            transaction.WriteJournal();
            ConfigWriter.WriteSpecs(root, "sei", specs);
            afterConfigWrite?.Invoke(transaction);
            return sync(transaction);
        }
        catch (Exception ex)
        {
            try
            {
                DeploymentTransaction.Recover(root, stateDirectory);
            }
            catch (Exception restore)
            {
                throw new SeikoException(
                    $"failed to restore the environment after a failed change: {restore.Message}",
                    "config",
                    ex);
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    private static string DefaultAddRange(NuGetVersion version)
    {
        var lower = version.ToNormalizedString();
        return version.Major == 0
            ? $"[{lower},0.{version.Minor + 1}.0)"
            : $"[{lower},{version.Major + 1}.0.0)";
    }

    private static void RequireOnline(bool offline, bool dryRun, string command)
    {
        if (offline && !dryRun)
        {
            throw new SeikoException(
                $"offline: {command} needs network metadata; use --dry-run --offline to preview from the cache",
                "source");
        }
    }

    private static SeiSpec ParseAddSpecification(string specification)
    {
        var at = specification.LastIndexOf('@');
        var name = (at > 0 ? specification[..at] : specification).Trim();
        var version = at > 0 ? specification[(at + 1)..].Trim() : null;
        if (string.IsNullOrWhiteSpace(name) || (at == specification.Length - 1))
        {
            throw new SeikoException("add requires a sei name and optional @range", "usage");
        }

        return new SeiSpec { Name = name, Version = version };
    }

    public SyncResult Remove(
        string root,
        string name,
        string? storeRoot = null,
        bool allowPrerelease = false,
        int jobs = DownloadSession.DefaultJobs,
        string? proxy = null,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool latestInstallable = false,
        bool allowCritical = false,
        bool dryRun = false,
        bool purge = false,
        bool adopt = false,
        bool force = false,
        bool offline = false)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SeikoException("remove requires a sei name", "usage");
        }

        RequireOnline(offline, dryRun, "remove");

        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        RecoverUnlessPreview(root, dryRun);

        var config = ConfigLoader.Load(root);
        var removedSei = config.Sei.RemoveAll(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)) > 0;
        if (!removedSei)
        {
            throw new SeikoException($"{name} is not a direct dependency", "resolve");
        }

        var existingLock = LockFile.Read(Path.Combine(root, "seiko.lock.json"));
        if (existingLock is not null)
        {
            using var session = new DownloadSession(jobs, cancellationToken);
            var sources = LoadSources(config, root, proxy, session, storePath, offline);
            using var sourceScope = new SourceScope(sources);
            var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
            var resolved = new Resolver(
                sources,
                existingLock,
                rid,
                allowPrerelease,
                updateNames: null,
                latestInstallable,
                allowCritical).Resolve(config.Sei);
            EnsureRemoveKeepsSelections(
                existingLock,
                resolved,
                new HashSet<string>(StringComparer.Ordinal) { name },
                new HashSet<string>(StringComparer.Ordinal));
        }

        if (dryRun)
        {
            return PreviewWithoutWriting(
                root,
                storePath,
                config.Sei,
                allowPrerelease,
                mode: SyncMode.Normal,
                jobs,
                proxy,
                progress,
                log,
                cancellationToken,
                updateNames: null,
                latestInstallable,
                allowCritical,
                adopt,
                force,
                offline);
        }

        return WriteSpecsAndSync(
            root,
            config.Sei,
            transaction => SyncLocked(
                root,
                storePath,
                allowPrerelease,
                SyncMode.Normal,
                jobs,
                proxy,
                progress,
                log,
                cancellationToken,
                updateNames: null,
                latestInstallable,
                allowCritical,
                transaction,
                adopt: adopt,
                force: force,
                offline: offline),
            purge
                ? transaction =>
                {
                    transaction.MoveToTrash(PurgeConfigPath(name));
                    transaction.MoveToTrash(PurgeDataPath(name));
                }
                : null);
    }

    internal static string PurgeConfigPath(string name)
        => $"config/seiko/{name}.conf";

    internal static string PurgeDataPath(string name)
        => $"data/seiko/{name}";

    public SyncResult Update(
        string root,
        string? name = null,
        bool latest = false,
        string? storeRoot = null,
        bool allowPrerelease = false,
        int jobs = DownloadSession.DefaultJobs,
        string? proxy = null,
        IProgress<SyncProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool latestInstallable = false,
        bool allowCritical = false,
        bool dryRun = false,
        bool adopt = false,
        bool force = false,
        bool offline = false)
    {
        root = Path.GetFullPath(root);
        if (latest && latestInstallable)
        {
            throw new SeikoException("--latest and --latest-installable are mutually exclusive", "usage");
        }

        RequireOnline(offline, dryRun, "update");

        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        RecoverUnlessPreview(root, dryRun);

        var config = ConfigLoader.Load(root);
        var targets = new List<SeiSpec>();
        if (string.IsNullOrWhiteSpace(name))
        {
            targets.AddRange(config.Sei);
        }
        else
        {
            var sei = config.Sei.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal));
            if (sei is null)
            {
                throw new SeikoException($"{name} is not a direct dependency", "resolve");
            }

            targets.Add(sei);
        }

        var writeConfig = latest && targets.Count > 0;
        if (writeConfig)
        {
            using var session = new DownloadSession(jobs, cancellationToken);
            var sources = LoadSources(config, root, proxy, session, storePath, offline);
            using var sourceScope = new SourceScope(sources);
            foreach (var target in targets)
            {
                var version = FindLatestVersion(sources, target.Name, allowPrerelease, allowCritical)
                    ?? throw new SeikoException($"no version of {target.Name} found in any source", "source")
                    {
                        Hint = "check the name, run `sk source status`, or allow pre-releases with --pre",
                        HintKey = "hint.source",
                    };
                target.Version = $"[{version}]";
            }
        }

        var updateNames = targets
            .Select(spec => spec.Name)
            .ToHashSet(StringComparer.Ordinal);
        SyncResult Sync(DeploymentTransaction? transaction) => SyncLocked(
            root,
            storePath,
            allowPrerelease,
            SyncMode.Normal,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames,
            latestInstallable,
            allowCritical,
            transaction,
            adopt: adopt,
            force: force,
            offline: offline);

        if (dryRun)
        {
            return PreviewWithoutWriting(
                root,
                storePath,
                config.Sei,
                allowPrerelease,
                mode: SyncMode.Normal,
                jobs,
                proxy,
                progress,
                log,
                cancellationToken,
                updateNames,
                latestInstallable,
                allowCritical,
                adopt,
                force,
                offline);
        }

        return writeConfig
            ? WriteSpecsAndSync(root, config.Sei, transaction => Sync(transaction))
            : Sync(null);
    }

    private static string? FindLatestVersion(
        IReadOnlyList<ISeiSource> sources,
        string name,
        bool allowPrerelease,
        bool allowCritical)
    {
        foreach (var source in sources)
        {
            var index = source.GetSei(name);
            if (index is null)
            {
                continue;
            }

            NuGetVersion? best = null;
            foreach (var entry in index.Versions)
            {
                if (entry.Yanked
                    || (Retirement.IsCritical(entry) && !allowCritical)
                    || !NuGetVersion.TryParse(entry.Version, out var version))
                {
                    continue;
                }

                if (!allowPrerelease && version.IsPrerelease)
                {
                    continue;
                }

                if (best is null || version > best)
                {
                    best = version;
                }
            }

            // The resolver takes every version of a name from the first source
            // that has it, so latest must be chosen from that source too.
            return best?.ToNormalizedString();
        }

        return null;
    }

    // Online diagnostics only; doctor stays local. A missing catalog is a
    // warning, an unreachable or invalid index is an error.
    public IReadOnlyList<SourceStatus> SourceStatus(
        string root,
        string? id = null,
        string? proxy = null,
        bool offline = false,
        string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root, requireSources: false);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        var result = new List<SourceStatus>();
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        foreach (var source in config.Sources)
        {
            if (id is not null && !string.Equals(source.Id, id, StringComparison.Ordinal))
            {
                continue;
            }

            var problems = new List<string>();
            ISeiSource? built = null;
            try
            {
                built = CreateSource(source, root, proxy, session, storePath, offline);
            }
            catch (SeikoException ex)
            {
                problems.Add(ex.Message);
            }

            var catalog = built switch
            {
                HttpSource http => http.Catalog,
                LocalSource local => local.Catalog,
                _ => null,
            };

            result.Add(new SourceStatus(
                source.Id,
                built is not null,
                built?.Index.Sei?.Count,
                catalog is not null,
                problems));
            (built as IDisposable)?.Dispose();
        }

        if (id is not null && result.Count == 0)
        {
            throw new SeikoException($"source '{id}' is not configured", "config");
        }

        return result;
    }

    public IReadOnlyList<SourceConfig> ListSources(string root)
    {
        root = Path.GetFullPath(root);
        return ConfigLoader.Load(root, requireSources: false).Sources;
    }

    public IReadOnlyList<SourceConfig> SourceAdd(
        string root,
        string id,
        string index,
        IReadOnlyList<string>? tokenHosts = null)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(index))
        {
            throw new SeikoException("source add requires id and index", "usage");
        }

        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        // A pending transaction holds an older config backup; editing first
        // would let the next recovery silently undo this command (ADR-0019).
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
        var config = ConfigLoader.Load(root, requireSources: false);
        if (config.Sources.Any(source => string.Equals(source.Id, id, StringComparison.Ordinal)))
        {
            throw new SeikoException($"source '{id}' already exists", "config");
        }

        var hosts = (tokenHosts ?? [])
            .Select(host => host.Trim())
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.Sources.Add(new SourceConfig
        {
            Id = id,
            Index = index,
            TokenHosts = hosts.Count > 0 ? hosts : null,
        });
        ConfigWriter.WriteSources(root, config.Sources);
        return config.Sources;
    }

    public IReadOnlyList<SourceConfig> SourceRemove(string root, string id)
    {
        root = Path.GetFullPath(root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
        var config = ConfigLoader.Load(root, requireSources: false);
        var removed = config.Sources.RemoveAll(source => string.Equals(source.Id, id, StringComparison.Ordinal));
        if (removed == 0)
        {
            throw new SeikoException($"source '{id}' not found", "source");
        }

        if (config.Sources.Count == 0)
        {
            throw new SeikoException("at least one source is required", "config");
        }

        ConfigWriter.WriteSources(root, config.Sources);
        return config.Sources;
    }

    public string? ConfigGet(string root, string key)
    {
        root = Path.GetFullPath(root);
        if (!string.Equals(key, "rid", StringComparison.Ordinal))
        {
            throw new SeikoException($"unsupported config key: {key}", "config");
        }

        return ConfigLoader.Load(root, requireSources: false).Rid;
    }

    public string? ConfigSet(string root, string key, string? value)
    {
        root = Path.GetFullPath(root);
        if (!string.Equals(key, "rid", StringComparison.Ordinal))
        {
            throw new SeikoException($"unsupported config key: {key}", "config");
        }

        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
        ConfigWriter.WriteRid(root, value);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public CacheStatusInfo CacheStatus(string? storeRoot = null)
    {
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        if (!Directory.Exists(storePath))
        {
            return new CacheStatusInfo(0, 0, 0, 0, 0);
        }

        var artifacts = 0;
        long bytes = 0;
        foreach (var directory in ArtifactDirectories(storePath))
        {
            artifacts++;
            bytes += DirectorySize(directory);
        }

        var staging = StoreStaging.Enumerate(storePath).ToList();
        var stagingBytes = staging.Sum(DirectorySize);
        var partialRoot = Path.Combine(storePath, ".partial");
        var partials = Directory.Exists(partialRoot)
            ? Directory.EnumerateFileSystemEntries(partialRoot).Count()
            : 0;
        return new CacheStatusInfo(artifacts, bytes, partials, staging.Count, stagingBytes);
    }

    public IReadOnlyList<CacheFinding> CacheVerify(string? storeRoot = null)
    {
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        var findings = new List<CacheFinding>();
        if (!Directory.Exists(storePath))
        {
            return findings;
        }

        foreach (var directory in ArtifactDirectories(storePath))
        {
            var artifact = Path.GetFileName(directory);
            using var artifactLock = ProcessLock.TryAcquire(
                Path.Combine(storePath, ".locks", artifact + ".lock"));
            if (artifactLock is null)
            {
                findings.Add(new CacheFinding(
                    "warning",
                    "cache-busy",
                    "artifact is busy; skipped",
                    directory));
                continue;
            }

            var manifestPath = Path.Combine(directory, "sei.json");
            if (!File.Exists(manifestPath))
            {
                findings.Add(new CacheFinding("error", "cache-missing", "artifact has no sei.json", directory));
                continue;
            }

            try
            {
                if (new FileInfo(manifestPath).Length > ArchiveLimits.MaxManifestBytes)
                {
                    findings.Add(new CacheFinding(
                        "error",
                        "cache-manifest",
                        $"sei.json exceeds the {ArchiveLimits.MaxManifestBytes}-byte limit",
                        manifestPath));
                    VerifyArtifactFiles(storePath, artifact, directory, findings);
                    continue;
                }

                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllBytes(manifestPath),
                    SeikoJsonContext.Default.SeiManifest);
                if (manifest is null || manifest.SchemaVersion != SeiManifest.CurrentSchemaVersion || string.IsNullOrWhiteSpace(manifest.Name))
                {
                    findings.Add(new CacheFinding("error", "cache-manifest", "invalid sei.json", directory));
                }
            }
            catch (Exception ex)
            {
                findings.Add(new CacheFinding("error", "cache-manifest", ex.Message, directory));
            }

            VerifyArtifactFiles(storePath, artifact, directory, findings);
        }

        foreach (var staging in StoreStaging.Enumerate(storePath))
        {
            if (!StoreStaging.IsStagingName(Path.GetFileName(staging), out var artifact))
            {
                continue;
            }

            using var artifactLock = StoreStaging.TryLock(storePath, artifact);
            if (artifactLock is null)
            {
                findings.Add(new CacheFinding(
                    "warning",
                    "cache-busy",
                    "artifact is busy; skipped",
                    staging));
                continue;
            }

            findings.Add(new CacheFinding(
                "warning",
                "cache-staging",
                "incomplete extraction; run `sk cache clean`",
                staging));
        }

        return findings;
    }

    private static void VerifyArtifactFiles(
        string storePath,
        string artifact,
        string directory,
        List<CacheFinding> findings)
    {
        if (PathSafety.IsLink(directory))
        {
            findings.Add(new CacheFinding(
                "error",
                "cache-corrupt",
                "artifact directory is a link",
                directory));
            return;
        }

        var files = new HashSet<string>(StringComparer.Ordinal);
        var links = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new CacheFinding("error", "io", ex.Message, current));
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                if (PathSafety.IsLink(entry))
                {
                    links.Add(relative);
                    findings.Add(new CacheFinding(
                        "error",
                        "cache-corrupt",
                        $"artifact path {relative} is a link",
                        entry));
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        files.Add(relative);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    findings.Add(new CacheFinding("error", "io", ex.Message, entry));
                }
            }
        }

        var state = StoreDigests.TryRead(storePath, artifact);
        if (state is null)
        {
            findings.Add(new CacheFinding(
                "warning",
                "cache-meta",
                "artifact has no valid digest record; run `sk sync` to repair it",
                directory));
            return;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in state.Files ?? [])
        {
            listed.Add(file.Path);
            if (links.Contains(file.Path))
            {
                continue;
            }

            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!files.Contains(file.Path))
            {
                findings.Add(new CacheFinding(
                    "error",
                    "cache-missing-file",
                    $"artifact file {file.Path} is missing",
                    directory));
                continue;
            }

            try
            {
                var actual = Hashing.Sha256File(path);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new CacheFinding(
                        "error",
                        "cache-corrupt",
                        $"artifact file {file.Path} has changed",
                        path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(new CacheFinding("error", "io", ex.Message, path));
            }
        }

        foreach (var file in files)
        {
            if (!listed.Contains(file))
            {
                findings.Add(new CacheFinding(
                    "warning",
                    "cache-unlisted",
                    $"artifact file {file} is not in the digest record",
                    Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
    }

    public CacheCleanResult CacheClean(string root, string? storeRoot = null)
    {
        root = RootRegistry.Resolve(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        // Hold the registry lock so no new root registers during the sweep,
        // and every registered root's install lock so none of them can sync or
        // publish a new lock between reading references and deleting.
        using var registryLock = ProcessLock.Acquire(
            Path.Combine(storePath, "roots.lock"),
            "store root registry");
        var roots = RootRegistry.Read(storePath)
            .Append(root)
            .Distinct(comparer)
            .Where(Directory.Exists)
            .OrderBy(path => path, comparer)
            .ToList();
        var rootLocks = new List<ProcessLock>();
        try
        {
            foreach (var registered in roots)
            {
                if (!Directory.Exists(registered))
                {
                    continue;
                }

                rootLocks.Add(ProcessLock.Acquire(
                    Path.Combine(registered, ".seiko", "install.lock"),
                    $"install root {registered}"));
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registered in roots)
            {
                foreach (var lockPath in RootLockPaths(registered))
                {
                    var lockFile = LockFile.Read(lockPath);
                    if (lockFile is null)
                    {
                        continue;
                    }

                    foreach (var sei in lockFile.Sei)
                    {
                        var digest = sei.Artifact.Sha256?.ToLowerInvariant();
                        if (Hashing.IsSha256(digest))
                        {
                            referenced.Add(digest!);
                        }
                    }
                }
            }

            if (referenced.Count == 0
                && LockFile.Read(Path.Combine(root, "seiko.lock.json")) is null)
            {
                throw new SeikoException("no lock; run `sk sync`", "lock");
            }

            var removed = 0;
            long bytes = 0;
            if (!Directory.Exists(storePath))
            {
                return new CacheCleanResult(0, 0);
            }

            foreach (var directory in ArtifactDirectories(storePath))
            {
                var name = Path.GetFileName(directory);
                if (referenced.Contains(name))
                {
                    continue;
                }

                try
                {
                    bytes += DirectorySize(directory);
                    Directory.Delete(directory, recursive: true);
                    StoreDigests.TryDelete(storePath, name);
                    removed++;
                }
                catch (IOException ex)
                {
                    throw new SeikoException($"could not remove {directory}: {ex.Message}", "io", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new SeikoException($"could not remove {directory}: {ex.Message}", "io", ex);
                }
            }

            foreach (var staging in StoreStaging.Enumerate(storePath))
            {
                var size = DirectorySize(staging);
                try
                {
                    if (StoreStaging.TryDelete(storePath, staging))
                    {
                        bytes += size;
                        removed++;
                    }
                }
                catch (IOException ex)
                {
                    throw new SeikoException($"could not remove {staging}: {ex.Message}", "io", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new SeikoException($"could not remove {staging}: {ex.Message}", "io", ex);
                }
            }

            StoreDigests.CleanOrphans(storePath);
            return new CacheCleanResult(removed, bytes);
        }
        finally
        {
            for (var index = rootLocks.Count - 1; index >= 0; index--)
            {
                rootLocks[index].Dispose();
            }
        }
    }

    private static IEnumerable<string> RootLockPaths(string root)
    {
        yield return Path.Combine(root, "seiko.lock.json");
        var generations = Path.Combine(root, ".seiko", "generations");
        if (!Directory.Exists(generations))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(generations))
        {
            yield return Path.Combine(directory, "lock.json");
        }
    }

    private static IEnumerable<string> ArtifactDirectories(string storePath)
        => Directory.EnumerateDirectories(storePath)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Length == 64 && name.All(Uri.IsHexDigit);
            })
            .OrderBy(path => path, StringComparer.Ordinal);

    private static long DirectorySize(string path)
    {
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                size += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }

        return size;
    }

    public RollbackResult Rollback(
        string root,
        string? storeRoot = null,
        int? toGeneration = null,
        string? proxy = null,
        bool offline = false)
    {
        root = Path.GetFullPath(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        var stateDirectory = Path.Combine(root, ".seiko");
        Deployer.Recover(root, stateDirectory);
        var statePath = Path.Combine(stateDirectory, "state.json");
        if (!File.Exists(statePath))
        {
            throw new SeikoException("no local state; run `sk sync` first", "lock");
        }

        var state = JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState)
            ?? throw new SeikoException("invalid local state", "lock");
        if (state.SchemaVersion != LocalState.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"unsupported deployment state schemaVersion {state.SchemaVersion}",
                "io");
        }

        var target = toGeneration ?? state.Generation - 1;
        if (target < 1)
        {
            throw new SeikoException("no previous generation to roll back to", "lock");
        }

        var targetSnapshot = ReadGenerationTarget(root, stateDirectory, target);
        var snapshotLock = targetSnapshot.Lock;
        var snapshotConfig = targetSnapshot.ConfigBytes;
        var snapshotConfigInfo = targetSnapshot.Config;
        var snapshotState = targetSnapshot.State;
        var adoptedSnapshots = targetSnapshot.AdoptedSnapshots;

        var store = new Store(storePath);
        var sei = new List<DeployedSei>();
        var needsFetch = false;
        foreach (var locked in snapshotLock.Sei)
        {
            StoredSei? stored;
            try
            {
                stored = store.TryGet(locked.Artifact.Sha256);
            }
            catch (SeikoException ex) when (ex.Code == "cache-repair")
            {
                needsFetch = true;
                break;
            }

            if (stored is null)
            {
                needsFetch = true;
                break;
            }

            if (!string.Equals(stored.Manifest.Name, locked.Name, StringComparison.Ordinal)
                || !NuGetVersion.TryParse(stored.Manifest.Version, out var storedVersion)
                || !NuGetVersion.TryParse(locked.Version, out var lockedVersion)
                || !storedVersion.Equals(lockedVersion))
            {
                throw new SeikoException($"rollback: stored manifest mismatch for {locked.Name} {locked.Version}", "io");
            }

            sei.Add(new DeployedSei(
                locked.Name,
                locked.Version,
                stored.Directory,
                stored.Manifest,
                stored.Digests));
        }

        if (needsFetch)
        {
            using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
            var sources = LoadSources(snapshotConfigInfo, root, proxy, session, storePath, offline);
            using var sourceScope = new SourceScope(sources);
            var specs = snapshotLock.Sei
                .Where(item => item.Direct)
                .Select(item => new SeiSpec { Name = item.Name, Version = $"[{item.Version}]" })
                .ToList();
            var resolved = LockReplay.Resolve(sources, snapshotLock, specs);
            sei = resolved
                .Select(item =>
                {
                    var stored = store.Ensure(item, session, CancellationToken.None);
                    return new DeployedSei(
                        item.Name,
                        item.Version,
                        stored.Directory,
                        stored.Manifest,
                        stored.Digests);
                })
                .ToList();
        }

        var deployment = Deployer.Deploy(
            root,
            sei,
            stateDirectory,
            snapshotLock,
            generationOverride: target,
            writeLock: true,
            restoreConfig: snapshotConfig,
            adoptedSnapshots: adoptedSnapshots);

        return new RollbackResult
        {
            Generation = target,
            SeiInstalled = snapshotLock.Sei.Count,
            FilesDeployed = deployment.FileCount,
        };
    }

    public RollbackPreview PreviewRollback(string root, int? toGeneration = null, string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        var stateDirectory = Path.Combine(root, ".seiko");
        var statePath = Path.Combine(stateDirectory, "state.json");
        if (!File.Exists(statePath))
        {
            throw new SeikoException("no local state; run `sk sync` first", "lock");
        }

        var state = JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState)
            ?? throw new SeikoException("invalid local state", "lock");
        var target = toGeneration ?? state.Generation - 1;
        if (target < 1)
        {
            throw new SeikoException("no previous generation to roll back to", "lock");
        }

        // The same target validation as list and the real rollback; a preview
        // cannot be greener than the operation it describes (ADR-0040).
        var targetSnapshot = ReadGenerationTarget(root, stateDirectory, target);
        var snapshotLock = targetSnapshot.Lock;
        var snapshotState = targetSnapshot.State;

        var currentConfig = File.Exists(Path.Combine(root, "seiko.conf"))
            ? File.ReadAllText(Path.Combine(root, "seiko.conf"))
            : "";
        var configChanges = DiffLines(currentConfig, targetSnapshot.ConfigText, "config");

        var currentLock = LockFile.Read(Path.Combine(root, "seiko.lock.json"))
            ?? throw new SeikoException("no lock; run `sk sync`", "lock");
        var lockChanges = DiffSei(currentLock, snapshotLock);

        var currentFiles = state.Files ?? new Dictionary<string, DeployedFile>(StringComparer.Ordinal);
        var targetFiles = snapshotState.Files ?? new Dictionary<string, DeployedFile>(StringComparer.Ordinal);
        var added = new List<string>();
        var removed = new List<string>();
        var replaced = new List<string>();
        var adopted = new List<string>();
        var missing = new List<string>();

        var store = new Store(storePath);
        // A real rollback ensures every artifact in the target lock, not the
        // deployed file hashes: those are content digests inside the artifact,
        // never store directory names. Map missing artifacts back to the files
        // they own so the preview names the same downloads (ADR-0040).
        var missingArtifacts = snapshotLock.Sei
            .Where(sei => !StoreHas(store, sei.Artifact.Sha256))
            .Select(sei => sei.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (path, record) in targetFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (record.Adopted)
            {
                adopted.Add(path);
                continue;
            }

            var existed = currentFiles.TryGetValue(path, out var current);
            if (!existed)
            {
                added.Add(path);
            }
            else if (!string.Equals(current!.Sha256, record.Sha256, StringComparison.Ordinal))
            {
                replaced.Add(path);
            }

            if (record.Owners.Any(missingArtifacts.Contains))
            {
                missing.Add(path);
            }
        }

        foreach (var path in currentFiles.Keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!targetFiles.ContainsKey(path))
            {
                removed.Add(path);
            }
        }

        return new RollbackPreview(
            state.Generation,
            target,
            configChanges,
            lockChanges,
            added,
            removed,
            replaced,
            adopted,
            missing.Distinct(StringComparer.Ordinal).ToList());
    }

    private static bool StoreHas(Store store, string sha256)
    {
        try
        {
            return store.TryGet(sha256) is not null;
        }
        catch (SeikoException)
        {
            return false;
        }
    }

    private static List<string> DiffLines(string current, string target, string label)
    {
        var before = current.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var after = target.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var changes = new List<string>();
        foreach (var line in after.Except(before, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                changes.Add($"+ {label}: {line}");
            }
        }

        foreach (var line in before.Except(after, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                changes.Add($"- {label}: {line}");
            }
        }

        return changes;
    }

    private static List<string> DiffSei(SeikoLock current, SeikoLock target)
    {
        var changes = new List<string>();
        var before = current.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        var after = target.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        foreach (var (name, locked) in after.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(name, out var old))
            {
                changes.Add($"+ sei: {name} {locked.Version}");
            }
            else if (!string.Equals(old.Version, locked.Version, StringComparison.Ordinal)
                || !string.Equals(old.Artifact.Sha256, locked.Artifact.Sha256, StringComparison.Ordinal))
            {
                changes.Add($"~ sei: {name} {old.Version} -> {locked.Version}");
            }
        }

        foreach (var name in before.Keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!after.ContainsKey(name))
            {
                changes.Add($"- sei: {name}");
            }
        }

        return changes;
    }

    public IReadOnlyList<GenerationInfo> Generations(string root, string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var stateDirectory = Path.Combine(root, ".seiko");
        var statePath = Path.Combine(stateDirectory, "state.json");
        LocalState? state = null;
        if (File.Exists(statePath))
        {
            try
            {
                state = JsonSerializer.Deserialize(
                    File.ReadAllBytes(statePath),
                    SeikoJsonContext.Default.LocalState);
            }
            catch (JsonException)
            {
            }
        }

        var current = state?.Generation ?? 0;
        var files = state?.Files ?? new Dictionary<string, DeployedFile>(StringComparer.Ordinal);
        var generations = new List<GenerationInfo>();
        var directory = Path.Combine(stateDirectory, "generations");
        if (!Directory.Exists(directory))
        {
            return generations;
        }

        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            if (!int.TryParse(Path.GetFileName(path), out var number))
            {
                continue;
            }

            var createdAt = ReadGenerationMeta(path);
            var valid = TryReadGenerationTarget(root, stateDirectory, number, out var snapshot);

            // The file count of the current generation comes from live state;
            // older ones from their own snapshot.
            var fileCount = number == current ? files.Count : snapshot?.State.Files?.Count ?? 0;
            generations.Add(new GenerationInfo(
                number,
                createdAt,
                number == current,
                snapshot?.Lock.Sei.Count ?? 0,
                fileCount,
                valid));
        }

        return generations.OrderByDescending(item => item.Generation).ToList();
    }

    // The read-only half of a rollback target, shared by `generation list`,
    // `rollback --dry-run` and the real rollback so the three cannot drift
    // (ADR-0040). Store presence is deliberately not checked: a missing
    // artifact can be downloaded, so it does not make a snapshot invalid.
    private sealed record GenerationTarget(
        SeikoLock Lock,
        LocalState State,
        byte[] ConfigBytes,
        string ConfigText,
        SeikoConfig Config,
        IReadOnlyDictionary<string, string> AdoptedSnapshots);

    private static GenerationTarget ReadGenerationTarget(string root, string stateDirectory, int target)
    {
        var directory = Path.Combine(stateDirectory, "generations", target.ToString());
        var lockPath = Path.Combine(directory, "lock.json");
        if (!File.Exists(lockPath))
        {
            throw new SeikoException($"generation {target} not found", "lock");
        }

        var lockFile = LockFile.Read(lockPath)
            ?? throw new SeikoException($"generation {target} has no lock", "lock");

        var configPath = Path.Combine(directory, "seiko.conf");
        PathSafety.EnsureNoLinks(
            root,
            Path.GetRelativePath(root, configPath).Replace('\\', '/'));
        if (!File.Exists(configPath))
        {
            throw new SeikoException($"generation {target} has no config", "io");
        }

        var configBytes = File.ReadAllBytes(configPath);
        var configText = File.ReadAllText(configPath);
        var config = ConfigLoader.LoadPath(configPath);
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
        if (!string.Equals(lockFile.Target.Rid, rid, StringComparison.Ordinal))
        {
            throw new SeikoException(
                $"generation {target} config selects rid '{rid}', but its lock targets '{lockFile.Target.Rid}'",
                "io");
        }

        // The target generation's own state is what defines its deployment,
        // so a generation without one cannot be restored faithfully.
        var statePath = Path.Combine(directory, "state.json");
        if (!File.Exists(statePath))
        {
            throw new SeikoException($"generation {target} has no state", "io");
        }

        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(statePath),
            SeikoJsonContext.Default.LocalState)
            ?? throw new SeikoException($"generation {target} has no state", "io");
        if (state.SchemaVersion != LocalState.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"generation {target} has unsupported state schemaVersion {state.SchemaVersion}",
                "io");
        }

        if (state.Files is null)
        {
            throw new SeikoException($"generation {target} has no deployment state", "io");
        }

        var adoptedSnapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, record) in state.Files)
        {
            if (record is null)
            {
                throw new SeikoException(
                    $"generation {target} has no record for {relative}",
                    "io");
            }

            if (!record.Adopted)
            {
                continue;
            }

            var snapshot = Path.Combine(directory, "adopted", relative);
            if (!File.Exists(snapshot))
            {
                throw new SeikoException(
                    $"generation {target} is missing the adopted snapshot of {relative}",
                    "io");
            }

            // A damaged snapshot must not silently redefine the generation:
            // the recorded digest is the authority, not today's bytes.
            if (!string.Equals(Hashing.Sha256File(snapshot), record.Sha256, StringComparison.Ordinal))
            {
                throw new SeikoException(
                    $"generation {target} snapshot of adopted file {relative} is corrupted",
                    "checksum");
            }

            adoptedSnapshots[relative] = snapshot;
        }

        return new GenerationTarget(lockFile, state, configBytes, configText, config, adoptedSnapshots);
    }

    private static bool TryReadGenerationTarget(
        string root,
        string stateDirectory,
        int target,
        out GenerationTarget? value)
    {
        try
        {
            value = ReadGenerationTarget(root, stateDirectory, target);
            return true;
        }
        catch (SeikoException)
        {
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        value = null;
        return false;
    }

    private static DateTimeOffset? ReadGenerationMeta(string directory)
    {
        var path = Path.Combine(directory, "meta.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.GenerationMeta)?.CreatedAt;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<SeiSummary> Search(
        string root,
        string term,
        string? proxy = null,
        bool offline = false,
        string? storeRoot = null,
        string? tag = null,
        string? license = null,
        int? limit = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        if (offline)
        {
            // Search visits each source's own list; keep that association so a
            // source that never lists a name is not required to cache it.
            OfflineMetadata.VerifySearch(config, storePath, root);
        }

        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session, storePath, offline);
        using var sourceScope = new SourceScope(sources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<(int Rank, SeiSummary Summary)>();

        foreach (var source in sources)
        {
            // One catalog read replaces one request per name. When the source
            // has none, the per-name path below still runs unchanged.
            var catalog = source.Catalog;
            var catalogued = catalog?.Sei
                .ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            foreach (var name in source.Index.Sei ?? new List<string>())
            {
                if (seen.Contains(name))
                {
                    continue;
                }

                if (catalogued is not null && catalogued.TryGetValue(name, out var cached))
                {
                    seen.Add(name);
                    if (cached.Unlisted || cached.Version is null)
                    {
                        continue;
                    }

                    if (!Matches(term, name, cached.Description, cached.Tags, out var cachedRank))
                    {
                        continue;
                    }

                    if (tag is not null
                        && !(cached.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (license is not null
                        && !string.Equals(cached.License, license, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    results.Add((cachedRank, new SeiSummary(
                        name,
                        cached.Version,
                        source.Id,
                        cached.Description,
                        cached.Tags,
                        cached.License,
                        cached.Deprecated,
                        cached.Advisory)));
                    continue;
                }

                var sei = source.GetSei(name);
                if (sei is null)
                {
                    continue;
                }

                // The first source that has the sei file owns the name, even
                // when every version is filtered out by retirement rules.
                seen.Add(name);
                if (sei.Unlisted)
                {
                    continue;
                }

                var latest = LatestVersion(sei);
                if (latest is null)
                {
                    continue;
                }

                if (!Matches(term, name, sei.Project, out var rank))
                {
                    continue;
                }

                if (tag is not null
                    && !(sei.Project?.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (license is not null
                    && !string.Equals(sei.Project?.License, license, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((rank, new SeiSummary(
                    name,
                    latest.Version,
                    source.Id,
                    sei.Project?.Description,
                    sei.Project?.Tags,
                    sei.Project?.License,
                    sei.Deprecated,
                    // Package-wide, matching the catalog, not just the latest
                    // version; otherwise a derived file changes the answer.
                    CatalogFile.MostSevere(sei.Versions))));
            }
        }

        // Exact name beats a prefix, a prefix beats a substring, and project
        // text is the weakest signal; ties stay alphabetical.
        var ordered = results
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Summary.Name, StringComparer.Ordinal)
            .Select(item => item.Summary);
        return (limit is > 0 ? ordered.Take(limit.Value) : ordered).ToList();
    }

    private static bool Matches(string term, string name, ProjectMetadata? project, out int rank)
        => Matches(term, name, project?.Description, project?.Tags, out rank);

    private static bool Matches(
        string term,
        string name,
        string? description,
        IReadOnlyList<string>? tags,
        out int rank)
    {
        if (string.Equals(name, term, StringComparison.OrdinalIgnoreCase))
        {
            rank = 0;
            return true;
        }

        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            rank = 1;
            return true;
        }

        if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            rank = 2;
            return true;
        }

        if ((tags ?? []).Any(tag => string.Equals(tag, term, StringComparison.OrdinalIgnoreCase)))
        {
            rank = 3;
            return true;
        }

        if ((description ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            rank = 4;
            return true;
        }

        rank = int.MaxValue;
        return false;
    }

    public SeiInfo Info(string root, string name, string? proxy = null, bool offline = false, string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        if (offline)
        {
            OfflineMetadata.Verify(config, storePath, [name]);
        }

        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session, storePath, offline);
        using var sourceScope = new SourceScope(sources);

        foreach (var source in sources)
        {
            var sei = source.GetSei(name);
            if (sei is null)
            {
                continue;
            }

            var versions = sei.Versions
                .OrderByDescending(entry => NuGetVersion.TryParse(entry.Version, out var parsed) ? parsed : new NuGetVersion(0, 0, 0))
                .Select(entry => new SeiVersionInfo(
                    entry.Version,
                    entry.Yanked,
                    entry.Deprecated,
                    entry.Advisory,
                    entry.Dependencies,
                    entry.Prerequisites,
                    entry.OptionalPeers,
                    entry.Conflicts,
                    (entry.Artifacts ?? [])
                        .Where(artifact => artifact is not null)
                        .Select(artifact => new SeiArtifactInfo(
                            artifact.Rid,
                            artifact.Dependencies ?? [],
                            artifact.Prerequisites ?? [],
                            artifact.OptionalPeers ?? [],
                            artifact.Conflicts ?? [],
                            artifact.ExternalFiles is not null || artifact.ExternalArchives is not null
                                ? ExternalFiles.Count(new ArtifactManifest
                                {
                                    ExternalFiles = artifact.ExternalFiles,
                                    ExternalArchives = artifact.ExternalArchives,
                                })
                                : artifact.External?.Files + artifact.External?.Archives ?? 0,
                            artifact.ExternalFiles is not null || artifact.ExternalArchives is not null
                                ? ExternalFiles.Hosts(new ArtifactManifest
                                {
                                    ExternalFiles = artifact.ExternalFiles,
                                    ExternalArchives = artifact.ExternalArchives,
                                }).ToList()
                                : artifact.External?.Hosts ?? [],
                            artifact.External is not null
                                || artifact.ExternalFiles is not null
                                || artifact.ExternalArchives is not null))
                        .ToList(),
                    IndexOverlay.IsSourceOrigin(entry)))
                .ToList();
            return new SeiInfo(
                name,
                source.Id,
                sei.Unlisted,
                sei.Deprecated,
                versions,
                sei.Project,
                sei.Upstream is null || !Uri.TryCreate(sei.Upstream.Url, UriKind.Absolute, out var upstream)
                    ? null
                    : upstream.Host,
                sei.Upstream is not null);
        }

        throw new SeikoException($"sei '{name}' not found in any source", "source")
        {
            Hint = "check the spelling, run `sk search <term>`, or add a source that carries it",
            HintKey = "hint.source",
        };
    }

    public IReadOnlyList<IReadOnlyList<string>> Why(string root, string name)
        => WhyDetailed(root, name)
            .Select(chain => (IReadOnlyList<string>)chain.Select(link => link.Name).ToList())
            .ToList();

    // A chain link records how the edge was declared: direct declarations,
    // plain dependencies, prerequisites and optional peers all keep a target
    // present or explain why it is required.
    public IReadOnlyList<IReadOnlyList<WhyLink>> WhyDetailed(string root, string name)
    {
        root = Path.GetFullPath(root);
        var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"))
            ?? throw new SeikoException("no lock; run `sk sync`", "lock");
        var byName = lockFile.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        if (!byName.ContainsKey(name))
        {
            throw new SeikoException($"sei '{name}' is not in the lock", "resolve");
        }

        var chains = new List<IReadOnlyList<WhyLink>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var direct in lockFile.Sei.Where(sei => sei.Direct).OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            var path = new List<WhyLink>();
            var pending = new Stack<WhyFrame>();
            pending.Push(new WhyFrame(direct.Name, "direct"));
            while (pending.Count > 0)
            {
                var frame = pending.Peek();
                if (!frame.Entered)
                {
                    frame.Entered = true;
                    if (path.Any(link => string.Equals(link.Name, frame.Name, StringComparison.Ordinal)))
                    {
                        pending.Pop();
                        continue;
                    }

                    path.Add(new WhyLink(frame.Name, frame.Kind));
                    if (string.Equals(frame.Name, name, StringComparison.Ordinal))
                    {
                        var key = string.Join("\0", path.Select(link => $"{link.Kind}:{link.Name}"));
                        if (seen.Add(key))
                        {
                            chains.Add(path.ToList());
                        }

                        path.RemoveAt(path.Count - 1);
                        pending.Pop();
                        continue;
                    }

                    if (!byName.TryGetValue(frame.Name, out var locked))
                    {
                        path.RemoveAt(path.Count - 1);
                        pending.Pop();
                        continue;
                    }

                    frame.Edges = Edges(locked);
                }

                if (frame.Edges is not null && frame.Index < frame.Edges.Count)
                {
                    var (dependency, kind) = frame.Edges[frame.Index++];
                    pending.Push(new WhyFrame(dependency.Name, kind));
                    continue;
                }

                path.RemoveAt(path.Count - 1);
                pending.Pop();
            }
        }

        if (chains.Count == 0)
        {
            throw new SeikoException($"no dependency path to {name}", "resolve");
        }

        return chains;
    }

    // Dependencies first (they are the only kind that adds a target), then the
    // kinds that merely constrain an already present one.
    private static List<(SeiDependency Edge, string Kind)> Edges(LockedSei locked)
        => locked.Dependencies.Select(edge => (edge, "dependency"))
            .Concat(locked.Artifact.Dependencies.Select(edge => (edge, "artifact dependency")))
            .Concat(locked.Prerequisites.Select(edge => (edge, "prerequisite")))
            .Concat(locked.Artifact.Prerequisites.Select(edge => (edge, "artifact prerequisite")))
            .Concat(locked.OptionalPeers.Select(edge => (edge, "optional peer")))
            .Concat(locked.Artifact.OptionalPeers.Select(edge => (edge, "artifact optional peer")))
            .ToList();

    private sealed class WhyFrame
    {
        public WhyFrame(string name, string kind)
        {
            Name = name;
            Kind = kind;
        }

        public string Name { get; }
        public string Kind { get; }
        public List<(SeiDependency Edge, string Kind)>? Edges { get; set; }
        public int Index { get; set; }
        public bool Entered { get; set; }
    }

    // Audit reads the per-version mirror entries, not the catalog: only a
    // version entry knows whether the installed version is affected. A source
    // that cannot answer makes the package `unknown`, never "clean".
    public AuditResult Audit(
        string root,
        string? proxy = null,
        bool offline = false,
        string? storeRoot = null,
        bool securityOnly = false)
    {
        root = Path.GetFullPath(root);
        var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"))
            ?? throw new SeikoException("no lock; run `sk sync`", "lock");
        var config = ConfigLoader.Load(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        // One unavailable source must not abort the whole audit: a locked
        // package whose source cannot be opened is `unknown`, and sources that
        // no lock entry uses are irrelevant (ADR-0039).
        var sources = new List<ISeiSource>();
        foreach (var sourceConfig in config.Sources)
        {
            try
            {
                sources.Add(CreateSource(sourceConfig, root, proxy, session, storePath, offline));
            }
            catch (Exception ex) when (ex is SeikoException or JsonException)
            {
                // A malformed index is as unusable as an unreachable one.
            }
        }

        using var sourceScope = new SourceScope(sources);
        var sourcesById = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);

        var findings = new List<AuditFinding>();
        var unknown = new List<string>();
        foreach (var locked in lockFile.Sei.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            // The lock records which source supplied the installed version.
            // Auditing another source that happens to carry the same name and
            // version would hide an advisory the locked source already has.
            SeiIndex? index;
            try
            {
                if (!sourcesById.TryGetValue(locked.Source, out var source))
                {
                    unknown.Add(locked.Name);
                    continue;
                }

                index = source.GetSei(locked.Name);
            }
            catch (Exception ex) when (ex is SeikoException or JsonException)
            {
                unknown.Add(locked.Name);
                continue;
            }

            // A syntactically valid but structurally broken mirror (null
            // version list or null entries) cannot answer the audit; treat it
            // as unknown rather than letting it throw outside the boundary.
            if (index is null
                || index.Versions is null
                || index.Versions.Any(item => item is null))
            {
                unknown.Add(locked.Name);
                continue;
            }

            var entry = index.Versions
                .FirstOrDefault(item => string.Equals(item.Version, locked.Version, StringComparison.Ordinal));
            if (entry is null)
            {
                unknown.Add(locked.Name);
                continue;
            }

            var fix = index.Versions
                .Where(item => !item.Yanked && item.Advisory is null
                    && NuGetVersion.TryParse(item.Version, out var parsed)
                    && parsed.CompareTo(locked.Version) > 0)
                .OrderByDescending(item => NuGetVersion.Parse(item.Version))
                .Select(item => item.Version)
                .FirstOrDefault();

            if (entry.Advisory is { } advisory)
            {
                findings.Add(new AuditFinding(
                    locked.Name,
                    locked.Version,
                    "advisory",
                    advisory.Severity,
                    advisory.Url,
                    fix));
            }

            if (!securityOnly)
            {
                if (entry.Yanked)
                {
                    findings.Add(new AuditFinding(locked.Name, locked.Version, "yanked", null, null, fix));
                }

                if (!string.IsNullOrWhiteSpace(entry.Deprecated))
                {
                    findings.Add(new AuditFinding(
                        locked.Name,
                        locked.Version,
                        "deprecated",
                        null,
                        null,
                        fix));
                }
            }
        }

        return new AuditResult(findings, unknown);
    }

    public IReadOnlyList<OutdatedSei> Outdated(
        string root,
        string? proxy = null,
        bool allowPrerelease = false,
        bool offline = false,
        string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"));
        if (lockFile is null)
        {
            return [];
        }

        var config = ConfigLoader.Load(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session, storePath, offline);
        using var sourceScope = new SourceScope(sources);
        var rangeTexts = config.Sei
            .GroupBy(spec => spec.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(spec => spec.Version).ToList(),
                StringComparer.Ordinal);
        var dependencyRanges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var locked in lockFile.Sei)
        {
            // The resolver enforces version-level and artifact-level relations
            // together, so the chosen artifact's dependencies constrain an
            // upgrade exactly like the version-level ones (ADR-0027).
            foreach (var dependency in locked.Dependencies.Concat(locked.Artifact.Dependencies))
            {
                if (!dependencyRanges.TryGetValue(dependency.Name, out var ranges))
                {
                    ranges = [];
                    dependencyRanges[dependency.Name] = ranges;
                }

                ranges.Add(dependency.Version ?? "");
            }
        }
        var result = new List<OutdatedSei>();

        foreach (var locked in lockFile.Sei.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            SeiIndex? index = null;
            foreach (var source in sources)
            {
                index = source.GetSei(locked.Name);
                if (index is not null)
                {
                    break;
                }
            }

            if (index is null)
            {
                continue;
            }

            var range = VersionRange.All;
            var parsedRanges = new List<VersionRange>();
            if (rangeTexts.TryGetValue(locked.Name, out var texts))
            {
                AddRanges(parsedRanges, texts, locked.Name);
            }

            if (dependencyRanges.TryGetValue(locked.Name, out var dependencyTexts))
            {
                AddRanges(parsedRanges, dependencyTexts, locked.Name);
            }

            if (parsedRanges.Count > 0)
            {
                range = VersionRange.CommonSubSet(parsedRanges);
            }

            NuGetVersion? latest = null;
            NuGetVersion? latestInRange = null;
            var rid = string.IsNullOrWhiteSpace(lockFile.Target.Rid) ? DefaultRid() : lockFile.Target.Rid;
            // A range that names a prerelease admits them, exactly as the
            // resolver decides; requiring --pre too would hide updates on a
            // prerelease line the user already chose (ADR-0004).
            var allowPrereleaseHere = allowPrerelease || Resolver.RangeAllowsPrerelease(range);
            var candidates = new List<NuGetVersion>();
            foreach (var entry in index.Versions)
            {
                if (entry.Yanked || Retirement.IsCritical(entry)
                    || !NuGetVersion.TryParse(entry.Version, out var version))
                {
                    continue;
                }

                if (!allowPrereleaseHere && version.IsPrerelease)
                {
                    continue;
                }

                if (latest is null || version > latest)
                {
                    latest = version;
                }

                if (range.Satisfies(version))
                {
                    candidates.Add(version);
                    if (latestInRange is null || version > latestInRange)
                    {
                        latestInRange = version;
                    }
                }
            }

            NuGetVersion? latestInstallable = null;
            foreach (var version in candidates.OrderByDescending(candidate => candidate))
            {
                if (IsInstallable(
                    config,
                    sources,
                    lockFile,
                    locked.Name,
                    version,
                    rid,
                    allowPrereleaseHere))
                {
                    latestInstallable = version;
                    break;
                }
            }

            if (latest is null
                || !NuGetVersion.TryParse(locked.Version, out var current)
                || (latestInRange is not null && latestInRange > current)
                || latest > current)
            {
                if (latest is not null)
                {
                    // Blockers describe constraints on this sei, not the ones
                    // it declares: the direct range, other locked sei that
                    // depend on it or conflict with it, and platform limits on
                    // the latest candidate.
                    var blockers = new List<OutdatedBlocker>();
                    foreach (var text in rangeTexts.GetValueOrDefault(locked.Name) ?? [])
                    {
                        blockers.Add(new OutdatedBlocker("declaration", locked.Name, text, locked.Source));
                    }

                    foreach (var owner in lockFile.Sei)
                    {
                        foreach (var dependency in owner.Dependencies
                            .Concat(owner.Artifact.Dependencies)
                            .Where(dependency => string.Equals(dependency.Name, locked.Name, StringComparison.Ordinal)))
                        {
                            blockers.Add(new OutdatedBlocker(
                                "dependency",
                                owner.Name,
                                dependency.Version,
                                owner.Source));
                        }

                        foreach (var conflict in owner.Conflicts
                            .Where(conflict => string.Equals(conflict.Name, locked.Name, StringComparison.Ordinal)))
                        {
                            blockers.Add(new OutdatedBlocker(
                                "conflict",
                                owner.Name,
                                conflict.Version,
                                owner.Source));
                        }
                    }

                    var latestEntry = index.Versions.FirstOrDefault(entry =>
                        NuGetVersion.TryParse(entry.Version, out var parsed) && parsed.Equals(latest));
                    if (latestEntry is not null
                        && latestEntry.Artifacts.All(artifact =>
                            !string.IsNullOrWhiteSpace(artifact.Rid)
                            && !artifact.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase)))
                    {
                        blockers.Add(new OutdatedBlocker("rid", rid, null, locked.Source));
                    }

                    result.Add(new OutdatedSei(
                        locked.Name,
                        locked.Version,
                        latestInRange?.ToNormalizedString(),
                        latest.ToNormalizedString(),
                        latestInstallable?.ToNormalizedString(),
                        blockers));
                }
            }
        }

        return result;
    }

    private static void EnsureRemoveKeepsSelections(
        SeikoLock existing,
        IReadOnlyList<ResolvedSei> resolved,
        IReadOnlySet<string> removed,
        IReadOnlySet<string> authorized)
    {
        var byName = resolved.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        foreach (var locked in existing.Sei)
        {
            if (removed.Contains(locked.Name)
                || authorized.Contains(locked.Name)
                || !byName.TryGetValue(locked.Name, out var current))
            {
                continue;
            }

            if (string.Equals(locked.Version, current.Version, StringComparison.Ordinal)
                && string.Equals(
                    locked.Artifact.Sha256,
                    current.Artifact.Source.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    locked.Artifact.Rid,
                    current.Artifact.Rid,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var removedText = string.Join(", ", removed.OrderBy(name => name, StringComparer.Ordinal));
            throw new SeikoException(
                $"remove {removedText} would change {locked.Name} from {locked.Version}/{locked.Artifact.Sha256} to {current.Version}/{current.Artifact.Source.Sha256}; use `add {locked.Name} --remove {removedText}` to authorize re-resolution, or `add P --remove {removedText}` to keep the current artifact",
                "resolve");
        }
    }

    private static bool IsInstallable(
        SeikoConfig config,
        IReadOnlyList<ISeiSource> sources,
        SeikoLock lockFile,
        string name,
        NuGetVersion version,
        string rid,
        bool allowPrerelease)
    {
        var specs = config.Sei
            .Select(spec => new SeiSpec { Name = spec.Name, Version = spec.Version })
            .ToList();
        var direct = specs.Where(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)).ToList();
        if (direct.Count == 0)
        {
            specs.Add(new SeiSpec { Name = name, Version = $"[{version}]" });
        }
        else
        {
            foreach (var spec in direct)
            {
                spec.Version = $"[{version}]";
            }
        }

        try
        {
            var resolver = new Resolver(
                sources,
                lockFile,
                rid,
                allowPrerelease,
                updateNames: new HashSet<string>(StringComparer.Ordinal) { name },
                latestInstallable: true);
            resolver.Resolve(specs);
            return true;
        }
        catch (SeikoException ex) when (ex.Code == "resolve")
        {
            return false;
        }
    }

    private static void AddRanges(
        List<VersionRange> parsed,
        IEnumerable<string?> texts,
        string name)
    {
        foreach (var rangeText in texts)
        {
            if (string.IsNullOrWhiteSpace(rangeText) || rangeText.Trim() == "*")
            {
                continue;
            }

            if (!VersionRange.TryParse(rangeText, out var one))
            {
                throw new SeikoException(
                    $"invalid version range '{rangeText}' for {name}",
                    "config");
            }

            parsed.Add(one);
        }
    }

    // The one "latest" rule shared by search (with or without a catalog) and
    // the catalog generator: stable wins over a numerically higher prerelease
    // (ADR-0004).
    internal static SeiVersionEntry? LatestVersion(SeiIndex sei)
    {
        var candidates = (sei.Versions ?? [])
            .Where(entry => entry is not null)
            .Select(entry => new
            {
                Entry = entry,
                Version = NuGetVersion.TryParse(entry.Version, out var parsed) ? parsed : null,
            })
            .Where(item => item.Version is not null
                && !item.Entry.Yanked
                && !Retirement.IsCritical(item.Entry))
            .ToList();

        var stable = candidates
            .Where(item => !item.Version!.IsPrerelease)
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        return (stable ?? candidates.OrderByDescending(item => item.Version).FirstOrDefault())?.Entry;
    }

    private static List<ISeiSource> LoadSources(
        SeikoConfig config,
        string root,
        string? proxy,
        DownloadSession session,
        string? storePath = null,
        bool offline = false)
        => config.Sources
            .Select(source => CreateSource(source, root, proxy, session, storePath, offline))
            .ToList();

    private static ISeiSource CreateSource(
        SourceConfig source,
        string root,
        string? proxy,
        DownloadSession session,
        string? storePath = null,
        bool offline = false)
    {
        if (source.Index.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.Index.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpSource(source, proxy, session, storePath, offline);
        }

        return new LocalSource(source, root);
    }

    public SeikoLock? ReadLock(string root)
        => LockFile.Read(Path.Combine(Path.GetFullPath(root), "seiko.lock.json"));

    public static string DefaultRid()
        => DefaultRidFor(
            OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "osx"
                : "unknown",
            RuntimeInformation.ProcessArchitecture);

    internal static string DefaultRidFor(string os, Architecture architecture)
    {
        var value = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown",
        };
        return $"{os}-{value}";
    }

    public static string CanonicalizePath(string path) => RootRegistry.Resolve(path);

    public static string DefaultStorePath()
    {
        var configured = Environment.GetEnvironmentVariable("SEIKO_STORE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "seiko",
                "store");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "seiko");
    }
}
