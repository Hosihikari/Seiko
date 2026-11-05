using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Seiko;

public sealed record DoctorFinding(string Severity, string Code, string Message, string? Path, string? Hint = null, string? HintKey = null)
{
    // The CLI localises the display text from these (ADR-0034); JSON keeps
    // the English Message and never exposes the keys.
    [JsonIgnore]
    public string? MessageKey { get; init; }

    [JsonIgnore]
    public object[]? MessageArgs { get; init; }
}

public sealed record DoctorResult(IReadOnlyList<DoctorFinding> Findings, int CleanCount);

public sealed class Doctor
{
    public DoctorResult Run(string root, string? storeRoot = null, bool clean = false)
    {
        root = Path.GetFullPath(root);
        var findings = new List<DoctorFinding>();
        using var installLock = ProcessLock.TryAcquire(
            Path.Combine(root, ".seiko", "install.lock"));
        if (installLock is null)
        {
            findings.Add(new DoctorFinding(
                "warning",
                "root-busy",
                "install root is in use by another process; run doctor again after it finishes",
                root)
            {
                MessageKey = "doctor.root-busy",
            });
            return new DoctorResult(findings, 0);
        }

        var config = TryLoadConfig(root, findings);
        var lockFile = TryReadLock(root, findings);
        var storePath = Path.GetFullPath(storeRoot ?? SeikoClient.DefaultStorePath());
        CheckLockMatchesConfig(config, lockFile, findings);
        CheckStore(lockFile, storePath, findings);
        CheckDeployment(root, lockFile, findings);
        CheckTransaction(root, findings);
        var cleanCount = clean
            ? CleanLeftovers(storePath, findings)
            : ReportLeftovers(storePath, findings);
        return new DoctorResult(findings, cleanCount);
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
                findings.Add(Warning("lock-missing", "no lock; run `sk sync`", path, "doctor.lock-missing"));
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

        var specs = config.Sei;
        var names = new HashSet<string>(specs.Select(spec => spec.Name), StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var locked = lockFile.Sei.FirstOrDefault(item => string.Equals(item.Name, spec.Name, StringComparison.Ordinal));
            if (locked is null)
            {
                findings.Add(Error(
                    "lock-missing-entry",
                    $"{spec.Name} is declared but not in the lock",
                    null,
                    "doctor.lock-missing-entry",
                    spec.Name));
                continue;
            }

            if (!locked.Direct)
            {
                findings.Add(Error(
                    "lock-direct",
                    $"{spec.Name} is declared but locked as transitive",
                    null,
                    "doctor.lock-direct",
                    spec.Name));
            }

            if (string.IsNullOrWhiteSpace(spec.Version) || spec.Version.Trim() == "*")
            {
                continue;
            }

            if (!VersionRange.TryParse(spec.Version, out var range))
            {
                findings.Add(Error(
                    "config",
                    $"invalid version range '{spec.Version}' for {spec.Name}",
                    null,
                    "doctor.invalid-range",
                    spec.Version,
                    spec.Name));
                continue;
            }

            if (!NuGetVersion.TryParse(locked.Version, out var version) || !range.Satisfies(version))
            {
                findings.Add(Error(
                    "lock-version",
                    $"{spec.Name} locks {locked.Version}, which does not satisfy {spec.Version}",
                    null,
                    "doctor.lock-version",
                    spec.Name,
                    locked.Version,
                    spec.Version));
            }
        }

        foreach (var locked in lockFile.Sei.Where(item => item.Direct))
        {
            if (!names.Contains(locked.Name))
            {
                findings.Add(Warning(
                    "lock-extra",
                    $"{locked.Name} is direct in the lock but not declared",
                    null,
                    "doctor.lock-extra",
                    locked.Name));
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
                findings.Add(Error(
                    "store-digest",
                    $"{sei.Name} has an invalid artifact digest",
                    null,
                    "doctor.store-digest",
                    sei.Name));
                continue;
            }

            // Another root may be replacing this artifact under its artifact
            // lock. Hold the same lock so a transient missing manifest is not
            // reported as a corrupted store.
            using var artifactLock = ProcessLock.TryAcquire(
                Path.Combine(storePath, ".locks", digest! + ".lock"));
            if (artifactLock is null)
            {
                findings.Add(Warning(
                    "store-busy",
                    $"{sei.Name} {sei.Version} is being updated by another process; run doctor again",
                    Path.Combine(storePath, digest!),
                    "doctor.store-busy",
                    sei.Name,
                    sei.Version));
                continue;
            }

            var directory = Path.Combine(storePath, digest!);
            var manifestPath = Path.Combine(directory, "sei.json");
            if (!File.Exists(manifestPath))
            {
                findings.Add(Error(
                    "store-missing",
                    $"{sei.Name} {sei.Version} is not in the store",
                    directory,
                    "doctor.store-missing",
                    sei.Name,
                    sei.Version));
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
                    findings.Add(Error(
                        "store-manifest",
                        $"{sei.Name} {sei.Version} manifest does not match the lock",
                        directory,
                        "doctor.store-manifest",
                        sei.Name,
                        sei.Version));
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
                findings.Add(Warning(
                    "deploy-missing-state",
                    "deployment state is missing; run `sk sync`",
                    statePath,
                    "doctor.deploy-missing-state"));
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
            findings.Add(Error(
                "deploy-state",
                "invalid deployment state",
                statePath,
                "doctor.deploy-state-invalid"));
            return;
        }

        if (state.SchemaVersion != LocalState.CurrentSchemaVersion)
        {
            // v0 does not read old state, so checking old file records would
            // report findings against ownership the next sync ignores.
            findings.Add(Error(
                "deploy-state",
                $"unsupported deployment state schemaVersion {state.SchemaVersion}",
                statePath,
                "doctor.deploy-state-schema",
                state.SchemaVersion));
            return;
        }

        if (state.Files is null)
        {
            findings.Add(Error(
                "deploy-state",
                "invalid deployment state: files is null",
                statePath,
                "doctor.deploy-state-files"));
            return;
        }

        foreach (var (relative, file) in state.Files)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (file is null)
            {
                findings.Add(Error(
                    "deploy-state",
                    $"deployed file {relative} has no record",
                    statePath,
                    "doctor.deploy-record",
                    relative));
                continue;
            }

            if (!File.Exists(path))
            {
                findings.Add(Error(
                    "deploy-missing-file",
                    $"deployed file {relative} is missing",
                    path,
                    "doctor.deploy-missing-file",
                    relative));
                continue;
            }

            try
            {
                var actual = Hashing.Sha256File(path);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(Error(
                        "deploy-hash",
                        $"deployed file {relative} has changed",
                        path,
                        "doctor.deploy-hash",
                        relative));
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
                journal,
                "doctor.transaction"));
        }
        else
        {
            var backup = Path.Combine(root, ".seiko", "transaction");
            if (Directory.Exists(backup))
            {
                findings.Add(Warning(
                    "deploy-backup",
                    "leftover deployment backup; run `sk sync` or remove it",
                    backup,
                    "doctor.backup"));
            }
        }
    }

    private static int ReportLeftovers(string storePath, List<DoctorFinding> findings)
    {
        var partialRoot = Path.Combine(storePath, ".partial");
        if (Directory.Exists(partialRoot))
        {
            var count = Directory.EnumerateFileSystemEntries(partialRoot).Count();
            if (count > 0)
            {
                findings.Add(Warning(
                    "partial",
                    $"{count} partial download item(s); run `sk doctor --clean`",
                    partialRoot,
                    "doctor.partial",
                    count));
            }
        }

        var staging = CountStaleStaging(storePath);
        if (staging > 0)
        {
            findings.Add(Warning(
                "staging",
                $"{staging} interrupted extraction(s); run `sk doctor --clean`",
                storePath,
                "doctor.staging",
                staging));
        }

        return 0;
    }

    private static int CleanLeftovers(string storePath, List<DoctorFinding> findings)
    {
        var count = CleanPartials(storePath, findings);
        foreach (var staging in StoreStaging.Enumerate(storePath))
        {
            try
            {
                if (StoreStaging.TryDelete(storePath, staging))
                {
                    count++;
                }
                else if (Directory.Exists(staging))
                {
                    findings.Add(Warning(
                        "staging-in-use",
                        "interrupted extraction is in use by another process; skipped",
                        staging,
                        "doctor.staging-in-use"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(Error("io", ex.Message, staging));
            }
        }

        return count;
    }

    private static int CountStaleStaging(string storePath)
    {
        var count = 0;
        foreach (var staging in StoreStaging.Enumerate(storePath))
        {
            if (!StoreStaging.IsStagingName(Path.GetFileName(staging), out var artifact))
            {
                continue;
            }

            using var artifactLock = StoreStaging.TryLock(storePath, artifact);
            if (artifactLock is not null)
            {
                count++;
            }
        }

        return count;
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
                    path,
                    "doctor.partial-in-use",
                    name));
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

    private static DoctorFinding Error(
        string code,
        string message,
        string? path,
        string? messageKey = null,
        params object[] messageArgs)
        => new("error", code, message, path, HintFor(code), HintKeyFor(code))
        {
            MessageKey = messageKey,
            MessageArgs = messageArgs.Length == 0 ? null : messageArgs,
        };

    private static DoctorFinding Warning(
        string code,
        string message,
        string? path,
        string? messageKey = null,
        params object[] messageArgs)
        => new("warning", code, message, path, HintFor(code), HintKeyFor(code))
        {
            MessageKey = messageKey,
            MessageArgs = messageArgs.Length == 0 ? null : messageArgs,
        };

    // A fixed map keeps hints short and consistent across the finding sites.
    // The key lets the CLI show the hint in the UI language while the JSON
    // keeps the English text.
    private static string? HintKeyFor(string code) => code switch
    {
        "config" => "hint.config",
        "lock" or "lock-missing" => "hint.lock",
        "lock-missing-entry" or "lock-direct" => "hint.lock-mismatch",
        "store-missing" or "store-digest" or "store-manifest" => "hint.store",
        "deploy-missing-state" or "deploy-missing-file" or "deploy-hash" => "hint.deploy",
        "transaction" => "hint.transaction",
        "root-busy" => "hint.root-busy",
        _ => null,
    };

    private static string? HintFor(string code) => code switch
    {
        "config" => "fix seiko.conf, then run `sk sync`",
        "lock" or "lock-missing" => "run `sk sync` to rebuild the lock",
        "lock-missing-entry" or "lock-direct" => "run `sk sync` so the lock matches the declaration",
        "store-missing" or "store-digest" or "store-manifest" => "run `sk cache verify`, then `sk sync` to refetch",
        "deploy-missing-state" or "deploy-missing-file" or "deploy-hash" =>
            "run `sk sync --force` to restore the deployment, or `sk rollback` to go back",
        "transaction" => "run `sk sync` or `sk rollback` to finish the interrupted transaction",
        "root-busy" => "wait for the other process to finish, then run `sk doctor` again",
        _ => null,
    };
}
