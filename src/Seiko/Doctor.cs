using System.Text.Json;
using NuGet.Versioning;

namespace Seiko;

public sealed record DoctorFinding(string Severity, string Code, string Message, string? Path);

public sealed record DoctorResult(IReadOnlyList<DoctorFinding> Findings, int CleanCount);

public sealed class Doctor
{
    public DoctorResult Run(string root, string? storeRoot = null, bool clean = false)
    {
        root = Path.GetFullPath(root);
        var findings = new List<DoctorFinding>();
        ProcessLock? cleanLock = null;
        try
        {
            if (clean)
            {
                cleanLock = ProcessLock.Acquire(
                    Path.Combine(root, ".seiko", "install.lock"),
                    $"install root {root}");
            }

            var config = TryLoadConfig(root, findings);
            var lockFile = TryReadLock(root, findings);
            var storePath = Path.GetFullPath(storeRoot ?? SeikoClient.DefaultStorePath());
            CheckLockMatchesConfig(config, lockFile, findings);
            CheckStore(lockFile, storePath, findings);
            CheckDeployment(root, lockFile, findings);
            CheckTransaction(root, findings);
            var cleanCount = clean
                ? CleanPartials(storePath, findings)
                : ReportPartials(storePath, findings);
            return new DoctorResult(findings, cleanCount);
        }
        finally
        {
            cleanLock?.Dispose();
        }
    }

    private static SeikoConfig? TryLoadConfig(string root, List<DoctorFinding> findings)
    {
        try
        {
            return ConfigLoader.Load(root);
        }
        catch (Exception ex)
        {
            findings.Add(Error("config", ex.Message, Path.Combine(root, "seiko.conf")));
            return null;
        }
    }

    private static SeikoLock? TryReadLock(string root, List<DoctorFinding> findings)
    {
        var path = Path.Combine(root, "seiko.lock.json");
        try
        {
            var lockFile = LockFile.Read(path);
            if (lockFile is null)
            {
                findings.Add(Warning("lock-missing", "no lock; run `sk sync`", path));
            }

            return lockFile;
        }
        catch (Exception ex)
        {
            findings.Add(Error("lock", ex.Message, path));
            return null;
        }
    }

    private static void CheckLockMatchesConfig(
        SeikoConfig? config,
        SeikoLock? lockFile,
        List<DoctorFinding> findings)
    {
        if (config is null || lockFile is null)
        {
            return;
        }

        var specs = config.Sei.Concat(config.Components).ToList();
        var names = new HashSet<string>(specs.Select(spec => spec.Name), StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var locked = lockFile.Sei.FirstOrDefault(item => string.Equals(item.Name, spec.Name, StringComparison.Ordinal));
            if (locked is null)
            {
                findings.Add(Error("lock-missing-entry", $"{spec.Name} is declared but not in the lock", null));
                continue;
            }

            if (!locked.Direct)
            {
                findings.Add(Error("lock-direct", $"{spec.Name} is declared but locked as transitive", null));
            }

            if (string.IsNullOrWhiteSpace(spec.Version) || spec.Version.Trim() == "*")
            {
                continue;
            }

            if (!VersionRange.TryParse(spec.Version, out var range))
            {
                findings.Add(Error("config", $"invalid version range '{spec.Version}' for {spec.Name}", null));
                continue;
            }

            if (!NuGetVersion.TryParse(locked.Version, out var version) || !range.Satisfies(version))
            {
                findings.Add(Error(
                    "lock-version",
                    $"{spec.Name} locks {locked.Version}, which does not satisfy {spec.Version}",
                    null));
            }
        }

        foreach (var locked in lockFile.Sei.Where(item => item.Direct))
        {
            if (!names.Contains(locked.Name))
            {
                findings.Add(Warning(
                    "lock-extra",
                    $"{locked.Name} is direct in the lock but not declared",
                    null));
            }
        }
    }

    private static void CheckStore(SeikoLock? lockFile, string storePath, List<DoctorFinding> findings)
    {
        if (lockFile is null)
        {
            return;
        }

        foreach (var sei in lockFile.Sei)
        {
            var digest = sei.Artifact.Sha256?.ToLowerInvariant();
            if (!Hashing.IsSha256(digest))
            {
                findings.Add(Error("store-digest", $"{sei.Name} has an invalid artifact digest", null));
                continue;
            }

            var directory = Path.Combine(storePath, digest!);
            var manifestPath = Path.Combine(directory, "sei.json");
            if (!File.Exists(manifestPath))
            {
                findings.Add(Error("store-missing", $"{sei.Name} {sei.Version} is not in the store", directory));
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize(
                    File.ReadAllBytes(manifestPath),
                    SeikoJsonContext.Default.SeiManifest);
                if (manifest is null
                    || !string.Equals(manifest.Name, sei.Name, StringComparison.Ordinal)
                    || !NuGetVersion.TryParse(manifest.Version, out var manifestVersion)
                    || !NuGetVersion.TryParse(sei.Version, out var lockedVersion)
                    || !manifestVersion.Equals(lockedVersion))
                {
                    findings.Add(Error("store-manifest", $"{sei.Name} {sei.Version} manifest does not match the lock", directory));
                }
            }
            catch (Exception ex)
            {
                findings.Add(Error("store-manifest", ex.Message, directory));
            }
        }
    }

    private static void CheckDeployment(string root, SeikoLock? lockFile, List<DoctorFinding> findings)
    {
        var statePath = Path.Combine(root, ".seiko", "state.json");
        if (!File.Exists(statePath))
        {
            if (lockFile is not null && lockFile.Sei.Count > 0)
            {
                findings.Add(Warning("deploy-missing-state", "deployment state is missing; run `sk sync`", statePath));
            }

            return;
        }

        LocalState? state;
        try
        {
            state = JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState);
        }
        catch (Exception ex)
        {
            findings.Add(Error("deploy-state", ex.Message, statePath));
            return;
        }

        if (state is null)
        {
            findings.Add(Error("deploy-state", "invalid deployment state", statePath));
            return;
        }

        foreach (var (relative, file) in state.Files)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                findings.Add(Error("deploy-missing-file", $"deployed file {relative} is missing", path));
                continue;
            }

            try
            {
                var actual = Hashing.Sha256File(path);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(Error("deploy-hash", $"deployed file {relative} has changed", path));
                }
            }
            catch (IOException ex)
            {
                findings.Add(Error("io", ex.Message, path));
            }
        }
    }

    private static void CheckTransaction(string root, List<DoctorFinding> findings)
    {
        var journal = Path.Combine(root, ".seiko", "transaction.json");
        if (File.Exists(journal))
        {
            findings.Add(Error(
                "deploy-transaction",
                "an incomplete deployment transaction exists; run `sk sync` to roll it back",
                journal));
        }
        else
        {
            var backup = Path.Combine(root, ".seiko", "transaction");
            if (Directory.Exists(backup))
            {
                findings.Add(Warning(
                    "deploy-backup",
                    "leftover deployment backup; run `sk sync` or remove it",
                    backup));
            }
        }
    }

    private static int ReportPartials(string storePath, List<DoctorFinding> findings)
    {
        var partialRoot = Path.Combine(storePath, ".partial");
        if (!Directory.Exists(partialRoot))
        {
            return 0;
        }

        var count = Directory.EnumerateFileSystemEntries(partialRoot).Count();
        if (count > 0)
        {
            findings.Add(Warning(
                "partial",
                $"{count} partial download item(s); run `sk doctor --clean`",
                partialRoot));
        }

        return 0;
    }

    private static int CleanPartials(string storePath, List<DoctorFinding> findings)
    {
        var partialRoot = Path.Combine(storePath, ".partial");
        if (!Directory.Exists(partialRoot))
        {
            return 0;
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(partialRoot).ToList())
        {
            var name = Path.GetFileName(path);
            using var artifactLock = ProcessLock.TryAcquire(
                Path.Combine(storePath, ".locks", name + ".lock"));
            if (artifactLock is null)
            {
                findings.Add(Warning(
                    "partial-in-use",
                    $"partial download {name} is in use by another process; skipped",
                    path));
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(Error("io", ex.Message, path));
            }
        }

        return count;
    }

    private static DoctorFinding Error(string code, string message, string? path)
        => new("error", code, message, path);

    private static DoctorFinding Warning(string code, string message, string? path)
        => new("warning", code, message, path);
}
