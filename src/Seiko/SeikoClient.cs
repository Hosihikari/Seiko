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
        if (transaction is null)
        {
            Deployer.Recover(root, Path.Combine(root, ".seiko"));
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
            return new SyncResult
            {
                Lock = lockFile,
                FilesDeployed = 0,
                Warnings = warnings,
                Preview = Preview(existingLock, lockFile, resolved, mode, warnings),
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

        return new SyncResult
        {
            Lock = lockFile,
            FilesDeployed = deployment.FileCount,
            Warnings = warnings,
            Diagnostics = described,
        };
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

        return new PreviewResult
        {
            Changes = changes,
            Blocked = blocked,
            Diagnostics = diagnostics,
            Warnings = warnings,
        };
    }

    private static List<(ResolvedSei Sei, StoredSei Stored)> DownloadAll(
        Store store,
        IReadOnlyList<ResolvedSei> resolved,
        DownloadSession session,
        CancellationTokenSource downloadCts)
    {
        Exception? firstError = null;
        var tasks = resolved
            .Select(async sei =>
            {
                try
                {
                    return (Sei: sei, Stored: await store.EnsureAsync(sei, session, downloadCts.Token)
                        .ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                    downloadCts.Cancel();
                    throw;
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
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
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
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
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
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
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
                    ?? throw new SeikoException($"no version of {target.Name} found in any source", "source");
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

        var snapshotLockPath = Path.Combine(stateDirectory, "generations", target.ToString(), "lock.json");
        if (!File.Exists(snapshotLockPath))
        {
            throw new SeikoException($"generation {target} not found", "lock");
        }

        var snapshotLock = LockFile.Read(snapshotLockPath)
            ?? throw new SeikoException($"generation {target} has no lock", "lock");
        var snapshotConfigPath = Path.Combine(
            stateDirectory,
            "generations",
            target.ToString(),
            "seiko.conf");
        PathSafety.EnsureNoLinks(
            root,
            Path.GetRelativePath(root, snapshotConfigPath).Replace('\\', '/'));
        if (!File.Exists(snapshotConfigPath))
        {
            throw new SeikoException($"generation {target} has no config", "io");
        }

        var snapshotConfig = File.ReadAllBytes(snapshotConfigPath);
        var snapshotConfigInfo = ConfigLoader.LoadPath(snapshotConfigPath);
        var snapshotRid = string.IsNullOrWhiteSpace(snapshotConfigInfo.Rid)
            ? DefaultRid()
            : snapshotConfigInfo.Rid;
        if (!string.Equals(snapshotLock.Target.Rid, snapshotRid, StringComparison.Ordinal))
        {
            throw new SeikoException(
                $"generation {target} config selects rid '{snapshotRid}', but its lock targets '{snapshotLock.Target.Rid}'",
                "io");
        }

        // The target generation's own state is what defines its deployment,
        // so a generation without one cannot be restored faithfully.
        var adoptedSnapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        var snapshotStatePath = Path.Combine(stateDirectory, "generations", target.ToString(), "state.json");
        if (!File.Exists(snapshotStatePath))
        {
            throw new SeikoException($"generation {target} has no state", "io");
        }

        var snapshotState = JsonSerializer.Deserialize(
            File.ReadAllBytes(snapshotStatePath),
            SeikoJsonContext.Default.LocalState)
            ?? throw new SeikoException($"generation {target} has no state", "io");
        if (snapshotState.SchemaVersion != LocalState.CurrentSchemaVersion)
        {
            throw new SeikoException(
                $"generation {target} has unsupported state schemaVersion {snapshotState.SchemaVersion}",
                "io");
        }

        if (snapshotState.Files is null)
        {
            throw new SeikoException($"generation {target} has no deployment state", "io");
        }

        foreach (var (relative, record) in snapshotState.Files)
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

            var snapshot = Path.Combine(
                stateDirectory,
                "generations",
                target.ToString(),
                "adopted",
                relative);
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

    public IReadOnlyList<SeiSummary> Search(string root, string term, string? proxy = null, bool offline = false, string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        if (offline)
        {
            // search walks the root name list, so every listed entry needs a
            // cached file; collect them before constructing sources.
            OfflineMetadata.Verify(config, storePath, [], followIndexLists: true);
        }

        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session, storePath, offline);
        using var sourceScope = new SourceScope(sources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SeiSummary>();

        foreach (var source in sources)
        {
            foreach (var name in source.Index.Sei ?? new List<string>())
            {
                if (!name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || seen.Contains(name))
                {
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

                results.Add(new SeiSummary(name, latest.Version, source.Id));
            }
        }

        return results.OrderBy(result => result.Name, StringComparer.Ordinal).ToList();
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
                            artifact.External is not null
                                ? artifact.External.Files + artifact.External.Archives
                                : ExternalFiles.Count(new ArtifactManifest
                                {
                                    ExternalFiles = artifact.ExternalFiles,
                                    ExternalArchives = artifact.ExternalArchives,
                                }),
                            artifact.External?.Hosts ?? ExternalFiles.Hosts(new ArtifactManifest
                            {
                                ExternalFiles = artifact.ExternalFiles,
                                ExternalArchives = artifact.ExternalArchives,
                            }).ToList()))
                        .ToList()))
                .ToList();
            return new SeiInfo(name, source.Id, sei.Unlisted, sei.Deprecated, versions);
        }

        throw new SeikoException($"sei '{name}' not found in any source", "source");
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
            foreach (var dependency in locked.Dependencies)
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
            var candidates = new List<NuGetVersion>();
            foreach (var entry in index.Versions)
            {
                if (entry.Yanked || Retirement.IsCritical(entry)
                    || !NuGetVersion.TryParse(entry.Version, out var version))
                {
                    continue;
                }

                if (!allowPrerelease && version.IsPrerelease)
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
                    allowPrerelease))
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

    private static SeiVersionEntry? LatestVersion(SeiIndex sei)
    {
        var candidates = sei.Versions
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
