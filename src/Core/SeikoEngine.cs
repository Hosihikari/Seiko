using System.Runtime.InteropServices;

namespace Seiko.Core;

public sealed class SyncResult
{
    public required SeikoLock Lock { get; init; }
    public required int FilesProjected { get; init; }
    public int PackagesInstalled => Lock.Packages.Count;
}

public sealed class SeikoEngine
{
    public SyncResult Sync(string root, string? storeRoot = null, bool allowPrerelease = false)
    {
        root = Path.GetFullPath(root);
        var config = ConfigLoader.Load(root);
        var rid = string.IsNullOrWhiteSpace(config.Rid) ? DefaultRid() : config.Rid;
        var sources = config.Sources
            .Select(source => new LocalSource(source, root))
            .ToList();

        var lockPath = Path.Combine(root, "seiko.lock.json");
        var existingLock = LockFile.Read(lockPath);
        var resolver = new Resolver(sources, existingLock, rid, allowPrerelease);
        var specs = config.Packages.Concat(config.Components).ToList();
        var resolved = resolver.Resolve(specs);

        var store = new Store(storeRoot ?? DefaultStorePath());
        var stored = resolved
            .Select(package => new StoredResolved(package, store.Ensure(package)))
            .ToList();
        var projection = Projector.Apply(root, stored, Path.Combine(root, ".seiko"));

        var lockFile = LockFile.FromResolution(rid, sources, resolved);
        LockFile.Write(lockPath, lockFile);

        return new SyncResult
        {
            Lock = lockFile,
            FilesProjected = projection.FileCount,
        };
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
