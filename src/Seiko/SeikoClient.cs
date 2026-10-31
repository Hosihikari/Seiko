using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NuGet.Versioning;

namespace Seiko;

public sealed class SyncResult
{
    public required SeikoLock Lock { get; init; }
    public required int FilesDeployed { get; init; }
    public int SeiInstalled => Lock.Sei.Count;
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
    string Latest);

public sealed record CacheStatusInfo(int Artifacts, long Bytes, int PartialItems);

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
        IReadOnlySet<string>? updateNames = null)
    {
        root = Path.GetFullPath(root);
        cancellationToken.ThrowIfCancellationRequested();
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
            updateNames);
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
        IReadOnlySet<string>? updateNames)
    {
        Deployer.Recover(root, Path.Combine(root, ".seiko"));
        var config = ConfigLoader.Load(root);
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var session = new DownloadSession(jobs, downloadCts.Token)
        {
            Progress = progress,
            Log = log,
        };
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
        var sources = LoadSources(config, root, proxy, session);
        using var sourceScope = new SourceScope(sources);

        var lockPath = Path.Combine(root, "seiko.lock.json");
        var existingLock = LockFile.Read(lockPath);
        var specs = config.Sei.Concat(config.Components).ToList();

        IReadOnlyList<ResolvedSei> resolved;
        SeikoLock lockFile;

        if (mode == SyncMode.Frozen)
        {
            if (existingLock is null)
            {
                throw new SeikoException("--frozen requires seiko.lock.json", "lock");
            }

            resolved = LockReplay.Resolve(sources, existingLock, specs);
            lockFile = existingLock;
        }
        else
        {
            var resolver = new Resolver(sources, existingLock, rid, allowPrerelease, updateNames);
            resolved = resolver.Resolve(specs);
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
        var deployment = Deployer.Deploy(
            root,
            deployedSei,
            Path.Combine(root, ".seiko"),
            lockFile,
            writeLock: mode != SyncMode.Frozen);

        return new SyncResult
        {
            Lock = lockFile,
            FilesDeployed = deployment.FileCount,
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
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var at = specification.LastIndexOf('@');
        var name = (at > 0 ? specification[..at] : specification).Trim();
        var version = at > 0 ? specification[(at + 1)..].Trim() : null;
        if (string.IsNullOrWhiteSpace(name) || (at == specification.Length - 1))
        {
            throw new SeikoException("add requires a sei name and optional @range", "usage");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        var config = ConfigLoader.Load(root);
        if (config.Components.Any(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)))
        {
            throw new SeikoException($"{name} is already declared in components", "config");
        }

        var existing = config.Sei.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal));
        if (existing is null)
        {
            config.Sei.Add(new SeiSpec { Name = name, Version = version });
        }
        else
        {
            existing.Version = version;
        }

        ConfigWriter.WriteSpecs(root, "sei", config.Sei);
        return SyncLocked(
            root,
            storePath,
            allowPrerelease,
            SyncMode.Normal,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames: null);
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
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SeikoException("remove requires a sei name", "usage");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        var config = ConfigLoader.Load(root);
        var removedSei = config.Sei.RemoveAll(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)) > 0;
        var removedComponent = config.Components.RemoveAll(spec => string.Equals(spec.Name, name, StringComparison.Ordinal)) > 0;
        if (!removedSei && !removedComponent)
        {
            throw new SeikoException($"{name} is not a direct dependency", "resolve");
        }

        if (removedSei)
        {
            ConfigWriter.WriteSpecs(root, "sei", config.Sei);
        }

        if (removedComponent)
        {
            ConfigWriter.WriteSpecs(root, "components", config.Components);
        }

        return SyncLocked(
            root,
            storePath,
            allowPrerelease,
            SyncMode.Normal,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames: null);
    }

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
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        cancellationToken.ThrowIfCancellationRequested();
        var storePath = Path.GetFullPath(storeRoot ?? DefaultStorePath());
        RootRegistry.Register(storePath, root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
        var config = ConfigLoader.Load(root);
        var targets = new List<SeiSpec>();
        if (string.IsNullOrWhiteSpace(name))
        {
            targets.AddRange(config.Sei);
            targets.AddRange(config.Components);
        }
        else
        {
            var sei = config.Sei.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal));
            var component = config.Components.FirstOrDefault(spec => string.Equals(spec.Name, name, StringComparison.Ordinal));
            if (sei is not null)
            {
                targets.Add(sei);
            }

            if (component is not null)
            {
                targets.Add(component);
            }

            if (targets.Count == 0)
            {
                throw new SeikoException($"{name} is not a direct dependency", "resolve");
            }
        }

        if (latest && targets.Count > 0)
        {
            using var session = new DownloadSession(jobs, cancellationToken);
            var sources = LoadSources(config, root, proxy, session);
            using var sourceScope = new SourceScope(sources);
            foreach (var target in targets)
            {
                var version = FindLatestVersion(sources, target.Name, allowPrerelease)
                    ?? throw new SeikoException($"no version of {target.Name} found in any source", "source");
                target.Version = $"[{version}]";
            }

            ConfigWriter.WriteSpecs(root, "sei", config.Sei);
            ConfigWriter.WriteSpecs(root, "components", config.Components);
        }

        var updateNames = targets
            .Select(spec => spec.Name)
            .ToHashSet(StringComparer.Ordinal);
        return SyncLocked(
            root,
            storePath,
            allowPrerelease,
            SyncMode.Normal,
            jobs,
            proxy,
            progress,
            log,
            cancellationToken,
            updateNames);
    }

    private static string? FindLatestVersion(
        IReadOnlyList<ISeiSource> sources,
        string name,
        bool allowPrerelease)
    {
        NuGetVersion? best = null;
        foreach (var source in sources)
        {
            var index = source.GetSei(name);
            if (index is null)
            {
                continue;
            }

            foreach (var entry in index.Versions)
            {
                if (entry.Yanked || !NuGetVersion.TryParse(entry.Version, out var version))
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
        }

        return best?.ToNormalizedString();
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
        string? tokenEnv = null,
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
            TokenEnv = string.IsNullOrWhiteSpace(tokenEnv) ? null : tokenEnv,
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
            return new CacheStatusInfo(0, 0, 0);
        }

        var artifacts = 0;
        long bytes = 0;
        foreach (var directory in ArtifactDirectories(storePath))
        {
            artifacts++;
            bytes += DirectorySize(directory);
        }

        var partialRoot = Path.Combine(storePath, ".partial");
        var partials = Directory.Exists(partialRoot)
            ? Directory.EnumerateFileSystemEntries(partialRoot).Count()
            : 0;
        return new CacheStatusInfo(artifacts, bytes, partials);
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
            var manifestPath = Path.Combine(directory, "sei.json");
            if (!File.Exists(manifestPath))
            {
                findings.Add(new CacheFinding("error", "cache-missing", "artifact has no sei.json", directory));
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllBytes(manifestPath),
                    SeikoJsonContext.Default.SeiManifest);
                if (manifest is null || manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Name))
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

        return findings;
    }

    private static void VerifyArtifactFiles(
        string storePath,
        string artifact,
        string directory,
        List<CacheFinding> findings)
    {
        var state = StoreDigests.TryRead(storePath, artifact);
        if (state is null)
        {
            findings.Add(new CacheFinding(
                "warning",
                "cache-meta",
                "artifact has no digest record; run `sk sync` to rebuild it",
                directory));
            return;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in state.Files ?? [])
        {
            listed.Add(file.Path);
            var path = Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
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

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (!listed.Contains(relative))
            {
                findings.Add(new CacheFinding(
                    "warning",
                    "cache-unlisted",
                    $"artifact file {relative} is not in the digest record",
                    file));
            }
        }
    }

    public CacheCleanResult CacheClean(string root, string? storeRoot = null)
    {
        root = Path.GetFullPath(root);
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

    public RollbackResult Rollback(string root, string? storeRoot = null, int? toGeneration = null)
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

        var store = new Store(storePath);
        var sei = new List<DeployedSei>();
        foreach (var locked in snapshotLock.Sei)
        {
            var stored = store.TryGet(locked.Artifact.Sha256)
                ?? throw new SeikoException(
                    $"rollback: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} is not in the store",
                    "io");

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

        var deployment = Deployer.Deploy(
            root,
            sei,
            stateDirectory,
            snapshotLock,
            generationOverride: target,
            writeLock: true);

        return new RollbackResult
        {
            Generation = target,
            SeiInstalled = snapshotLock.Sei.Count,
            FilesDeployed = deployment.FileCount,
        };
    }

    public IReadOnlyList<SeiSummary> Search(string root, string term, string? proxy = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session);
        using var sourceScope = new SourceScope(sources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SeiSummary>();

        foreach (var source in sources)
        {
            foreach (var name in source.Index.Sei ?? new List<string>())
            {
                if (!name.Contains(term, StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                {
                    continue;
                }

                var sei = source.GetSei(name);
                var latest = sei is null ? null : LatestVersion(sei);
                if (latest is not null)
                {
                    results.Add(new SeiSummary(name, latest.Version, source.Id));
                }
            }
        }

        return results.OrderBy(result => result.Name, StringComparer.Ordinal).ToList();
    }

    public SeiInfo Info(string root, string name, string? proxy = null)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session);
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
                    entry.Dependencies,
                    entry.Artifacts.Count))
                .ToList();
            return new SeiInfo(name, source.Id, versions);
        }

        throw new SeikoException($"sei '{name}' not found in any source", "source");
    }

    public IReadOnlyList<IReadOnlyList<string>> Why(string root, string name)
    {
        root = Path.GetFullPath(root);
        var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"))
            ?? throw new SeikoException("no lock; run `sk sync`", "lock");
        var byName = lockFile.Sei.ToDictionary(sei => sei.Name, StringComparer.Ordinal);
        if (!byName.ContainsKey(name))
        {
            throw new SeikoException($"sei '{name}' is not in the lock", "resolve");
        }

        var chains = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var direct in lockFile.Sei.Where(sei => sei.Direct).OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            Visit(direct.Name, []);
        }

        if (chains.Count == 0)
        {
            throw new SeikoException($"no dependency path to {name}", "resolve");
        }

        return chains;

        void Visit(string current, List<string> path)
        {
            if (path.Contains(current, StringComparer.Ordinal))
            {
                return;
            }

            path.Add(current);
            if (string.Equals(current, name, StringComparison.Ordinal))
            {
                var key = string.Join("\0", path);
                if (seen.Add(key))
                {
                    chains.Add(path.ToList());
                }
            }
            else if (byName.TryGetValue(current, out var locked))
            {
                foreach (var dependency in locked.Dependencies.OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    Visit(dependency.Name, path);
                }
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    public IReadOnlyList<OutdatedSei> Outdated(
        string root,
        string? proxy = null,
        bool allowPrerelease = false)
    {
        root = Path.GetFullPath(root);
        var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"));
        if (lockFile is null)
        {
            return [];
        }

        var config = ConfigLoader.Load(root);
        using var session = new DownloadSession(DownloadSession.DefaultJobs, CancellationToken.None);
        var sources = LoadSources(config, root, proxy, session);
        using var sourceScope = new SourceScope(sources);
        var ranges = config.Sei.Concat(config.Components)
            .GroupBy(spec => spec.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.Ordinal);
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
            if (ranges.TryGetValue(locked.Name, out var rangeText)
                && !string.IsNullOrWhiteSpace(rangeText)
                && rangeText.Trim() != "*"
                && !VersionRange.TryParse(rangeText, out range))
            {
                throw new SeikoException($"invalid version range '{rangeText}' for {locked.Name}", "config");
            }

            NuGetVersion? latest = null;
            NuGetVersion? latestInRange = null;
            foreach (var entry in index.Versions)
            {
                if (entry.Yanked || !NuGetVersion.TryParse(entry.Version, out var version))
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

                if (range.Satisfies(version) && (latestInRange is null || version > latestInRange))
                {
                    latestInRange = version;
                }
            }

            if (latest is null
                || !NuGetVersion.TryParse(locked.Version, out var current)
                || (latestInRange is not null && latestInRange > current)
                || latest > current)
            {
                if (latest is not null)
                {
                    result.Add(new OutdatedSei(
                        locked.Name,
                        locked.Version,
                        latestInRange?.ToNormalizedString(),
                        latest.ToNormalizedString()));
                }
            }
        }

        return result;
    }

    private static SeiVersionEntry? LatestVersion(SeiIndex sei)
    {
        var candidates = sei.Versions
            .Select(entry => new
            {
                Entry = entry,
                Version = NuGetVersion.TryParse(entry.Version, out var parsed) ? parsed : null,
            })
            .Where(item => item.Version is not null && !item.Entry.Yanked)
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
        DownloadSession session)
        => config.Sources.Select(source => CreateSource(source, root, proxy, session)).ToList();

    private static ISeiSource CreateSource(
        SourceConfig source,
        string root,
        string? proxy,
        DownloadSession session)
    {
        if (source.Index.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.Index.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpSource(source, proxy, session);
        }

        return new LocalSource(source, root);
    }

    public SeikoLock? ReadLock(string root)
        => LockFile.Read(Path.Combine(Path.GetFullPath(root), "seiko.lock.json"));

    public static string DefaultRid()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown",
        };
        return $"{os}-{architecture}";
    }

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
