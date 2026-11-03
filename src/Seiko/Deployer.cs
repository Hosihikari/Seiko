using System.Text;
using System.Text.Json;

namespace Seiko;

public sealed record DeployedSei(
    string Name,
    string Version,
    string Directory,
    SeiManifest Manifest,
    IReadOnlyDictionary<string, StoreFileDigest> Digests);

public sealed class DeployResult
{
    public required int FileCount { get; init; }
}

public static class Deployer
{
    internal static AsyncLocal<Action<int>?> AppliedForTests { get; } = new();

    public static void Recover(string root, string stateDirectory)
        => DeploymentTransaction.Recover(root, stateDirectory);

    public static DeployResult Deploy(
        string root,
        IReadOnlyList<DeployedSei> sei,
        string stateDirectory,
        SeikoLock lockFile,
        int? generationOverride = null,
        bool writeLock = false,
        byte[]? restoreConfig = null,
        bool adopt = false,
        bool force = false)
        => DeployCore(
            root,
            sei,
            stateDirectory,
            lockFile,
            generationOverride,
            writeLock,
            restoreConfig,
            transaction: null,
            adopt,
            force);

    internal static DeployResult DeployInTransaction(
        string root,
        IReadOnlyList<DeployedSei> sei,
        string stateDirectory,
        SeikoLock lockFile,
        DeploymentTransaction transaction,
        int? generationOverride = null,
        bool writeLock = false,
        byte[]? restoreConfig = null,
        bool adopt = false,
        bool force = false)
        => DeployCore(
            root,
            sei,
            stateDirectory,
            lockFile,
            generationOverride,
            writeLock,
            restoreConfig,
            transaction,
            adopt,
            force);

    private static DeployResult DeployCore(
        string root,
        IReadOnlyList<DeployedSei> sei,
        string stateDirectory,
        SeikoLock lockFile,
        int? generationOverride,
        bool writeLock,
        byte[]? restoreConfig,
        DeploymentTransaction? transaction,
        bool adopt,
        bool force)
    {
        root = Path.GetFullPath(root);
        stateDirectory = Path.GetFullPath(stateDirectory);
        var configPath = Path.Combine(root, "seiko.conf");
        if (restoreConfig is null && !File.Exists(configPath))
        {
            throw new SeikoException("missing seiko.conf; cannot record a generation", "config");
        }

        var configSnapshot = restoreConfig ?? File.ReadAllBytes(configPath);
        var desired = BuildDesired(sei);
        var statePath = Path.Combine(stateDirectory, "state.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState) ?? new LocalState()
            : new LocalState();
        if (state.SchemaVersion != LocalState.CurrentSchemaVersion)
        {
            // v0 does not read old local state: its ownership and generation
            // are ignored, the deployment starts again at 1, and files the
            // old state recorded are treated as unmanaged.
            state = new LocalState();
        }

        PathSafety.EnsureNoLinks(root, Relative(root, stateDirectory));
        foreach (var relative in desired.Keys)
        {
            PathSafety.EnsureNoLinks(root, relative);
        }

        foreach (var relative in state.Files.Keys)
        {
            PathSafety.EnsureNoLinks(root, relative);
        }

        var managed = new HashSet<string>(state.Files.Keys, StringComparer.Ordinal);
        var replaceable = ValidateDesiredTree(root, desired, managed, force);
        var adoptedHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        var mutations = new List<Mutation>();
        foreach (var oldFile in state.Files.Keys)
        {
            if (IsReserved(oldFile))
            {
                throw new SeikoException(
                    $"deployment state contains a reserved path: {oldFile}",
                    "deploy");
            }

            if (desired.ContainsKey(oldFile) || !File.Exists(Native(root, oldFile)))
            {
                continue;
            }

            var record = state.Files[oldFile];
            var edited = !string.Equals(
                Hashing.Sha256File(Native(root, oldFile)),
                record.Sha256,
                StringComparison.Ordinal);
            if (edited && !record.Adopted && !force && !adopt)
            {
                throw new SeikoException(
                    $"deployment conflict: managed file {oldFile} was modified; pass --force to delete it or --adopt to keep it",
                    "deploy");
            }

            // Adopted content is user state: keep it on disk and drop only the
            // ownership record. force still deletes; an untouched file is
            // cleaned up as before.
            if (!force && (record.Adopted || adopt))
            {
                continue;
            }

            mutations.Add(new Mutation(oldFile, null));
        }

        foreach (var (relative, file) in desired)
        {
            var destination = Native(root, relative);
            var exists = File.Exists(destination);
            var managedHere = state.Files.TryGetValue(relative, out var previous);
            var unchanged = false;
            if (exists)
            {
                var actual = Hashing.Sha256File(destination);
                if (!managedHere && !adopt && !force)
                {
                    throw new SeikoException(
                        $"deployment conflict: unmanaged path {relative} is in the way; pass --adopt to keep it or --force to overwrite it",
                        "deploy");
                }

                var modified = managedHere
                    && !previous!.Adopted
                    && !string.Equals(actual, previous.Sha256, StringComparison.Ordinal);
                if (modified && !adopt && !force)
                {
                    throw new SeikoException(
                        $"deployment conflict: managed file {relative} was modified; pass --force to overwrite it or --adopt to keep it",
                        "deploy");
                }

                var keepExisting = !force
                    && (adopt || (managedHere && previous!.Adopted));
                if (keepExisting)
                {
                    adoptedHashes[relative] = actual;
                    continue;
                }

                unchanged = string.Equals(actual, file.Sha256, StringComparison.Ordinal);
            }

            if (!unchanged)
            {
                mutations.Add(new Mutation(relative, file));
            }
        }

        var temporaryPaths = mutations
            .Where(mutation => mutation.File is not null)
            .ToDictionary(
                mutation => mutation.RelativePath,
                mutation => DeploymentTemporaryName(mutation.RelativePath),
                StringComparer.Ordinal);

        var modes = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);
        foreach (var (relative, file) in desired)
        {
            if (TryParseMode(relative, file.Modes, out var mode))
            {
                modes[relative] = mode;
            }
        }

        var mutatedPaths = mutations
            .Where(mutation => mutation.File is not null)
            .Select(mutation => mutation.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var modeChanges = modes
            .Where(pair => !mutatedPaths.Contains(pair.Key)
                && File.Exists(Native(root, pair.Key))
                && ModeDiffers(Native(root, pair.Key), pair.Value))
            .Select(pair => pair.Key)
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        var newState = new LocalState
        {
            Generation = generationOverride ?? state.Generation + 1,
            Files = desired.ToDictionary(
                pair => pair.Key,
                pair => new DeployedFile
                {
                    Owners = pair.Value.Owners.Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList(),
                    Sha256 = adoptedHashes.TryGetValue(pair.Key, out var kept)
                        ? kept
                        : pair.Value.Sha256,
                    Adopted = adoptedHashes.ContainsKey(pair.Key),
                },
                StringComparer.Ordinal),
        };
        var generationDirectory = Path.Combine(stateDirectory, "generations", newState.Generation.ToString());
        var installedPaths = sei
            .Select(item => Path.Combine(stateDirectory, "installed", item.Name, item.Version, "sei.json"))
            .ToList();

        var createdDirectories = new List<string>();
        try
        {
            transaction ??= DeploymentTransaction.Begin(root, stateDirectory);
            if (restoreConfig is not null)
            {
                transaction.WillChange("seiko.conf");
            }

            foreach (var mutation in mutations)
            {
                transaction.WillChange(
                    mutation.RelativePath,
                    allowDirectory: mutation.File is not null
                        && replaceable.Contains(mutation.RelativePath),
                    sha256: mutation.File?.Sha256);
            }

            foreach (var temporary in temporaryPaths.Values)
            {
                transaction.WillCreateTemporary(temporary);
            }

            transaction.WillChange(Relative(root, statePath));
            foreach (var installed in installedPaths)
            {
                transaction.WillChange(Relative(root, installed));
            }

            if (writeLock)
            {
                transaction.WillChange("seiko.lock.json");
            }

            if (Directory.Exists(generationDirectory))
            {
                transaction.WillChange(Relative(root, Path.Combine(generationDirectory, "state.json")));
                transaction.WillChange(Relative(root, Path.Combine(generationDirectory, "lock.json")));
                transaction.WillChange(Relative(root, Path.Combine(generationDirectory, "seiko.conf")));
            }
            else
            {
                transaction.SetNewGeneration(Relative(root, generationDirectory));
            }

            foreach (var relative in modeChanges)
            {
                transaction.WillChange(relative);
            }

            transaction.WriteJournal();
            if (restoreConfig is not null)
            {
                WriteAtomic(configPath, restoreConfig);
            }

            var applied = 0;
            foreach (var mutation in mutations)
            {
                if (mutation.File is null)
                {
                    File.Delete(Native(root, mutation.RelativePath));
                    AppliedForTests.Value?.Invoke(++applied);
                    continue;
                }

                var destination = Native(root, mutation.RelativePath);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    EnsureDirectory(parent, createdDirectories);
                }

                if (Directory.Exists(destination))
                {
                    DeploymentTransaction.RemoveEmptyDirectories(destination);
                    if (Directory.Exists(destination))
                    {
                        throw new SeikoException(
                            $"deployment path is a directory: {mutation.RelativePath}",
                            "deploy");
                    }
                }

                EnsureStoreUnchanged(mutation.File);
                var temp = Native(root, temporaryPaths[mutation.RelativePath]);
                try
                {
                    FileLink.Create(
                        mutation.File.SourcePath,
                        temp,
                        allowHardlink: !modes.ContainsKey(mutation.RelativePath));
                    File.Move(temp, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }

                AppliedForTests.Value?.Invoke(++applied);
            }

            foreach (var relative in modes.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (modeChanges.Contains(relative))
                {
                    // A mode-only change must not chmod a hardlink shared with
                    // the store; replace the deployed entry with an independent
                    // copy first.
                    transaction.Detach(relative);
                }

                ApplyMode(Native(root, relative), relative, modes[relative]);
            }

            WriteAtomic(statePath, JsonSerializer.Serialize(newState, SeikoJsonContext.Default.LocalState));
            WriteGeneration(stateDirectory, newState, lockFile, configSnapshot);
            WriteInstalledManifests(installedPaths, sei);
            if (writeLock)
            {
                LockFile.Write(Path.Combine(root, "seiko.lock.json"), lockFile);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            try
            {
                transaction?.RollbackAfterFailure(ex);
            }
            finally
            {
                CleanupCreatedDirectories(createdDirectories);
            }

            throw;
        }

        PruneGenerations(stateDirectory, newState.Generation);
        return new DeployResult { FileCount = desired.Count };
    }

    // A fixed-length leaf avoids exceeding a filesystem component limit when
    // the destination basename is already long.
    private static string DeploymentTemporaryName(string relative)
    {
        var slash = relative.LastIndexOf('/');
        var directory = slash < 0 ? "" : relative[..(slash + 1)];
        return directory + ".seiko-tmp-" + Guid.NewGuid().ToString("N");
    }

    private static Dictionary<string, DesiredFile> BuildDesired(IReadOnlyList<DeployedSei> sei)
    {
        var desired = new Dictionary<string, DesiredFile>(StringComparer.Ordinal);
        var caseInsensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sei)
        {
            foreach (var file in Directory.EnumerateFiles(item.Directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(item.Directory, file).Replace('\\', '/');
                if (relative is "sei.json")
                {
                    continue;
                }

                if (IsReserved(relative))
                {
                    throw new SeikoException(
                        $"sei '{item.Name}' declares a reserved path: {relative}",
                        "deploy");
                }

                var normalized = relative.Normalize(System.Text.NormalizationForm.FormC);
                if (caseInsensitive.TryGetValue(normalized, out var other)
                    && !string.Equals(other, relative, StringComparison.Ordinal))
                {
                    throw new SeikoException(
                        $"path conflict: {other} and {relative} differ only by case or Unicode normalization",
                        "deploy");
                }

                caseInsensitive[normalized] = relative;

                var hash = HashStoredFile(item, file, relative);
                if (desired.TryGetValue(relative, out var existing))
                {
                    if (!string.Equals(existing.Sha256, hash, StringComparison.Ordinal))
                    {
                        throw new SeikoException(
                            $"file conflict: {relative} differs between {existing.Owners[0]} and {item.Name}",
                            "deploy");
                    }

                    var existingMode = GetModeText(existing.Modes, relative);
                    var incomingMode = GetModeText(item.Manifest.Artifact?.Modes, relative);
                    if (existingMode is not null && incomingMode is not null
                        && ParseModeValue(relative, existingMode) != ParseModeValue(relative, incomingMode))
                    {
                        throw new SeikoException(
                            $"mode conflict: {relative} is {existingMode} for {string.Join(", ", existing.Owners)} and {incomingMode} for {item.Name}",
                            "deploy");
                    }

                    if (existingMode is null && incomingMode is not null)
                    {
                        existing.Modes = new Dictionary<string, string>(
                            existing.Modes ?? [],
                            StringComparer.Ordinal)
                        {
                            [relative] = incomingMode,
                        };
                    }

                    existing.Owners.Add(item.Name);
                    continue;
                }

                desired[relative] = new DesiredFile
                {
                    SourcePath = file,
                    StoreDirectory = item.Directory,
                    Sha256 = hash,
                    Owners = [item.Name],
                    Modes = item.Manifest.Artifact?.Modes,
                };
            }
        }

        return desired;
    }

    private static HashSet<string> ValidateDesiredTree(
        string root,
        IReadOnlyDictionary<string, DesiredFile> desired,
        IReadOnlySet<string> managed,
        bool force)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in desired.Keys)
        {
            normalized[relative.Normalize(System.Text.NormalizationForm.FormC)] = relative;
        }

        var replaceable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in desired.Keys)
        {
            var destination = Native(root, relative);
            if (Directory.Exists(destination))
            {
                EnsureReplaceableDirectory(root, relative, destination, managed, desired, force);
                replaceable.Add(relative);
            }

            var parts = relative.Split('/');
            var prefix = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? parts[i] : $"{prefix}/{parts[i]}";
                if (normalized.TryGetValue(
                        prefix.Normalize(System.Text.NormalizationForm.FormC),
                        out var file))
                {
                    throw new SeikoException(
                        $"path conflict: {file} is both a file and a directory of {relative}",
                        "deploy");
                }

                if (File.Exists(Native(root, prefix))
                    && !(managed.Contains(prefix) && !desired.ContainsKey(prefix)))
                {
                    throw new SeikoException(
                        $"path conflict: {prefix} is a file but {relative} needs a directory",
                        "deploy");
                }
            }
        }

        return replaceable;
    }

    private static void EnsureReplaceableDirectory(
        string root,
        string relative,
        string destination,
        IReadOnlySet<string> managed,
        IReadOnlyDictionary<string, DesiredFile> desired,
        bool force)
    {
        var allowedDirectories = new HashSet<string>(StringComparer.Ordinal) { relative };
        var prefix = relative + "/";
        foreach (var managedPath in managed)
        {
            if (!managedPath.StartsWith(prefix, StringComparison.Ordinal)
                || desired.ContainsKey(managedPath))
            {
                continue;
            }

            var slash = managedPath.LastIndexOf('/');
            while (slash > relative.Length)
            {
                var directory = managedPath[..slash];
                if (!allowedDirectories.Add(directory))
                {
                    break;
                }

                slash = directory.LastIndexOf('/');
            }
        }

        var pending = new Stack<string>();
        pending.Push(destination);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SeikoException(
                        $"path conflict: {relative} contains a link at {Path.GetRelativePath(root, entry)}",
                        "deploy");
                }

                var child = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!allowedDirectories.Contains(child) && !force)
                    {
                        throw new SeikoException(
                            $"path conflict: {relative} is a directory containing unmanaged directory {child}; pass --force to replace it",
                            "deploy");
                    }

                    if (!force || allowedDirectories.Contains(child))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (!managed.Contains(child) || desired.ContainsKey(child))
                {
                    if (!force)
                    {
                        throw new SeikoException(
                            $"path conflict: {relative} is a directory containing unmanaged file {child}; pass --force to replace it",
                            "deploy");
                    }
                }
            }
        }
    }

    private static void EnsureDirectory(string path, List<string> created)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var missing = new Stack<string>();
        var current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        Directory.CreateDirectory(path);
        created.AddRange(missing);
    }

    private static void CleanupCreatedDirectories(List<string> created)
    {
        for (var i = created.Count - 1; i >= 0; i--)
        {
            var directory = created[i];
            try
            {
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void WriteGeneration(
        string stateDirectory,
        LocalState state,
        SeikoLock lockFile,
        byte[] config)
    {
        var directory = Path.Combine(stateDirectory, "generations", state.Generation.ToString());
        Directory.CreateDirectory(directory);
        WriteAtomic(
            Path.Combine(directory, "state.json"),
            JsonSerializer.Serialize(state, SeikoJsonContext.Default.LocalState));
        LockFile.Write(Path.Combine(directory, "lock.json"), lockFile);
        WriteAtomic(Path.Combine(directory, "seiko.conf"), config);
    }

    private static void PruneGenerations(string stateDirectory, int generation)
    {
        var generations = Path.Combine(stateDirectory, "generations");
        if (!Directory.Exists(generations))
        {
            return;
        }

        foreach (var path in Directory.EnumerateDirectories(generations))
        {
            var name = Path.GetFileName(path);
            if (int.TryParse(name, out var value) && value <= generation - 3)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void WriteInstalledManifests(IReadOnlyList<string> paths, IReadOnlyList<DeployedSei> sei)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths[i])!);
            File.Copy(
                Path.Combine(sei[i].Directory, "sei.json"),
                paths[i],
                overwrite: true);
        }
    }

    private static string? GetModeText(Dictionary<string, string>? modes, string relative)
        => modes is not null && modes.TryGetValue(relative, out var text) ? text : null;

    private static int ParseModeValue(string relative, string text)
    {
        if (!FileModes.TryParse(text, out var value))
        {
            throw new SeikoException($"invalid mode '{text}' for {relative}", "deploy");
        }

        if (FileModes.HasSpecialBits(value))
        {
            throw new SeikoException(
                $"mode '{text}' for {relative} sets setuid/setgid/sticky; only 0000-0777 is allowed",
                "deploy");
        }

        return value;
    }

    private static bool TryParseMode(
        string relative,
        Dictionary<string, string>? modes,
        out UnixFileMode mode)
    {
        mode = default;
        if (modes is null || !modes.TryGetValue(relative, out var text))
        {
            return false;
        }

        var value = ParseModeValue(relative, text);
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        mode = (UnixFileMode)value;
        return true;
    }

    private static bool ModeDiffers(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return File.GetUnixFileMode(path) != mode;
    }

    private static void ApplyMode(string path, string relative, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SeikoException($"deployed file is missing: {relative}", "deploy", ex);
        }
    }

    private static bool IsReserved(string relative)
    {
        // Case-insensitive everywhere: macOS volumes are case-insensitive by
        // default, and a stricter check on case-sensitive filesystems is
        // harmless.
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return relative.Equals("seiko.conf", comparison)
            || relative.Equals("seiko.lock.json", comparison)
            || relative.Equals(".seiko", comparison)
            || relative.StartsWith(".seiko/", comparison);
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Native(string root, string relative)
        => RootedPath.Resolve(root, RelativePath.Parse(relative));

    private static void WriteAtomic(string path, string contents)
        => WriteAtomic(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    private static void WriteAtomic(string path, byte[] contents)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Mutation(string RelativePath, DesiredFile? File, bool Adopted = false);

    private static string HashStoredFile(DeployedSei sei, string file, string relative)
    {
        var actual = Hashing.Sha256File(file);
        if (sei.Digests.Count == 0)
        {
            return actual;
        }

        if (!sei.Digests.TryGetValue(relative, out var digest)
            || !actual.Equals(digest.Sha256, StringComparison.Ordinal))
        {
            throw CorruptStore(sei, relative);
        }

        return actual;
    }

    private static void EnsureStoreUnchanged(DesiredFile file)
    {
        var actual = Hashing.Sha256File(file.SourcePath);
        if (!actual.Equals(file.Sha256, StringComparison.Ordinal))
        {
            throw CorruptStore(file.StoreDirectory, file.SourcePath);
        }
    }

    private static SeikoException CorruptStore(DeployedSei sei, string relative)
        => CorruptStore(sei.Directory, Path.Combine(sei.Directory, relative));

    private static SeikoException CorruptStore(string storeDirectory, string path)
        => new(
            $"store artifact is corrupted ({path}); delete {storeDirectory} and run `sk sync` to download it again",
            "checksum");

    private sealed class DesiredFile
    {
        public required string SourcePath { get; init; }
        public required string StoreDirectory { get; init; }
        public required string Sha256 { get; init; }
        public required List<string> Owners { get; init; }
        public Dictionary<string, string>? Modes { get; set; }
    }
}
