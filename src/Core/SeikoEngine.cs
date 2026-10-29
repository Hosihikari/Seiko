using System.Runtime.InteropServices;
using System.Text.Json;
using NuGet.Versioning;

namespace Seiko.Core;

public sealed class SyncResult
{
    public required SeikoLock Lock { get; init; }
    public required int FilesProjected { get; init; }
    public int PackagesInstalled => Lock.Packages.Count;
}

public sealed class RollbackResult
{
    public required int Generation { get; init; }
    public required int PackagesInstalled { get; init; }
    public required int FilesProjected { get; init; }
}

public sealed class SeikoEngine
{
    public SyncResult Sync(string root, string? storeRoot = null, bool allowPrerelease = false, SyncMode mode = SyncMode.Normal)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
        var sources = LoadSources(config, root);

        var lockPath = Path.Combine(root, "seiko.lock.json");
        var existingLock = LockFile.Read(lockPath);
        var specs = config.Packages.Concat(config.Components).ToList();

        IReadOnlyList<ResolvedPackage> resolved;
        SeikoLock lockFile;

        if (mode == SyncMode.Frozen)
        {
            if (existingLock is null)
            {
                throw new SeikoException("--frozen requires seiko.lock.json");
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
                    throw new SeikoException("--locked requires seiko.lock.json");
                }

                var difference = LockReplay.Compare(existingLock, lockFile);
                if (difference is not null)
                {
                    throw new SeikoException($"--locked: lock is out of date ({difference})");
                }
            }
        }

        var store = new Store(storeRoot ?? DefaultStorePath());
        var stored = resolved
            .Select(package => (Package: package, Stored: store.Ensure(package)))
            .ToList();
        var projectionPackages = stored
            .Select(item => new ProjectionPackage(
                item.Package.Name,
                item.Package.Version,
                item.Stored.Directory,
                item.Stored.Manifest))
            .ToList();
        var projection = Projector.Apply(
            root,
            projectionPackages,
            Path.Combine(root, ".seiko"),
            lockFile);

        if (mode != SyncMode.Frozen)
        {
            LockFile.Write(lockPath, lockFile);
        }

        return new SyncResult
        {
            Lock = lockFile,
            FilesProjected = projection.FileCount,
        };
    }

    public RollbackResult Rollback(string root, string? storeRoot = null, int? toGeneration = null)
    {
        root = Path.GetFullPath(root);
        var stateDirectory = Path.Combine(root, ".seiko");
        var statePath = Path.Combine(stateDirectory, "state.json");
        if (!File.Exists(statePath))
        {
            throw new SeikoException("no local state; run `sk sync` first");
        }

        var state = JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState)
            ?? throw new SeikoException("invalid local state");

        var target = toGeneration ?? state.Generation - 1;
        if (target < 1)
        {
            throw new SeikoException("no previous generation to roll back to");
        }

        var snapshotLockPath = Path.Combine(stateDirectory, "generations", target.ToString(), "lock.json");
        if (!File.Exists(snapshotLockPath))
        {
            throw new SeikoException($"generation {target} not found");
        }

        var snapshotLock = LockFile.Read(snapshotLockPath)
            ?? throw new SeikoException($"generation {target} has no lock");

        var store = new Store(storeRoot ?? DefaultStorePath());
        var packages = new List<ProjectionPackage>();
        foreach (var locked in snapshotLock.Packages)
        {
            var stored = store.TryGet(locked.Artifact.Sha256)
                ?? throw new SeikoException(
                    $"rollback: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} is not in the store");

            if (!string.Equals(stored.Manifest.Name, locked.Name, StringComparison.Ordinal)
                || !NuGetVersion.TryParse(stored.Manifest.Version, out var storedVersion)
                || !NuGetVersion.TryParse(locked.Version, out var lockedVersion)
                || !storedVersion.Equals(lockedVersion))
            {
                throw new SeikoException($"rollback: stored manifest mismatch for {locked.Name} {locked.Version}");
            }

            packages.Add(new ProjectionPackage(locked.Name, locked.Version, stored.Directory, stored.Manifest));
        }

        var projection = Projector.Apply(
            root,
            packages,
            stateDirectory,
            snapshotLock,
            generationOverride: target);
        LockFile.Write(Path.Combine(root, "seiko.lock.json"), snapshotLock);

        return new RollbackResult
        {
            Generation = target,
            PackagesInstalled = snapshotLock.Packages.Count,
            FilesProjected = projection.FileCount,
        };
    }

    public IReadOnlyList<PackageSummary> Search(string root, string term)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var sources = LoadSources(config, root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<PackageSummary>();

        foreach (var source in sources)
        {
            foreach (var name in source.Index.Packages ?? new List<string>())
            {
                if (!name.Contains(term, StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                {
                    continue;
                }

                var package = source.GetPackage(name);
                var latest = package is null ? null : LatestVersion(package);
                if (latest is not null)
                {
                    results.Add(new PackageSummary(name, latest.Version, source.Id));
                }
            }
        }

        return results.OrderBy(result => result.Name, StringComparer.Ordinal).ToList();
    }

    public PackageInfo Info(string root, string name)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var sources = LoadSources(config, root);

        foreach (var source in sources)
        {
            var package = source.GetPackage(name);
            if (package is null)
            {
                continue;
            }

            var versions = package.Versions
                .OrderByDescending(entry => NuGetVersion.TryParse(entry.Version, out var parsed) ? parsed : new NuGetVersion(0, 0, 0))
                .Select(entry => new PackageVersionInfo(
                    entry.Version,
                    entry.Yanked,
                    entry.Deprecated,
                    entry.Dependencies,
                    entry.Artifacts.Count))
                .ToList();
            return new PackageInfo(name, source.Id, versions);
        }

        throw new SeikoException($"package '{name}' not found in any source");
    }

    private static PackageVersionEntry? LatestVersion(PackageIndex package)
    {
        var candidates = package.Versions
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

    private static List<IPackageSource> LoadSources(SeikoConfig config, string root)
        => config.Sources.Select(source => CreateSource(source, root)).ToList();

    private static IPackageSource CreateSource(SourceConfig source, string root)
    {
        if (source.Index.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.Index.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpSource(source);
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
