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
        CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        using var rootLock = ProcessLock.Acquire(
            Path.Combine(root, ".seiko", "install.lock"),
            $"install root {root}");
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
            var resolver = new Resolver(sources, existingLock, rid, allowPrerelease);
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

        var store = new Store(storeRoot ?? DefaultStorePath());
        var stored = DownloadAll(store, resolved, session, downloadCts);
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
                item.Stored.Manifest))
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

    public RollbackResult Rollback(string root, string? storeRoot = null, int? toGeneration = null)
    {
        root = Path.GetFullPath(root);
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

        var store = new Store(storeRoot ?? DefaultStorePath());
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

            sei.Add(new DeployedSei(locked.Name, locked.Version, stored.Directory, stored.Manifest));
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
