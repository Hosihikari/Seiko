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
        bool writeLock = false)
    {
        root = Path.GetFullPath(root);
        stateDirectory = Path.GetFullPath(stateDirectory);
        var desired = BuildDesired(sei);
        var statePath = Path.Combine(stateDirectory, "state.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState) ?? new LocalState()
            : new LocalState();

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
        var replaceable = ValidateDesiredTree(root, desired, managed);

        var mutations = new List<Mutation>();
        foreach (var oldFile in state.Files.Keys)
        {
            if (IsReserved(oldFile))
            {
                throw new SeikoException(
                    $"deployment state contains a reserved path: {oldFile}",
                    "deploy");
            }

            if (!desired.ContainsKey(oldFile) && File.Exists(Native(root, oldFile)))
            {
                mutations.Add(new Mutation(oldFile, null));
            }
        }

        foreach (var (relative, file) in desired)
        {
            var destination = Native(root, relative);
            var unchanged = File.Exists(destination)
                && string.Equals(Hashing.Sha256File(destination), file.Sha256, StringComparison.Ordinal);
            if (!unchanged)
            {
                mutations.Add(new Mutation(relative, file));
            }
        }

        var temporaryPaths = mutations
            .Where(mutation => mutation.File is not null)
            .ToDictionary(
                mutation => mutation.RelativePath,
                mutation => mutation.RelativePath + ".seiko-tmp-" + Guid.NewGuid().ToString("N"),
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
                    Sha256 = pair.Value.Sha256,
                },
                StringComparer.Ordinal),
        };
        var generationDirectory = Path.Combine(stateDirectory, "generations", newState.Generation.ToString());
        var installedPaths = sei
            .Select(item => Path.Combine(stateDirectory, "installed", item.Name, item.Version, "sei.json"))
            .ToList();

        DeploymentTransaction? transaction = null;
        var createdDirectories = new List<string>();
        try
        {
            transaction = DeploymentTransaction.Begin(root, stateDirectory);
            foreach (var mutation in mutations)
            {
                transaction.WillChange(
                    mutation.RelativePath,
                    allowDirectory: mutation.File is not null
                        && replaceable.Contains(mutation.RelativePath));
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
                    FileLink.Create(mutation.File.SourcePath, temp);
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
                ApplyMode(Native(root, relative), relative, modes[relative]);
            }

            WriteAtomic(statePath, JsonSerializer.Serialize(newState, SeikoJsonContext.Default.LocalState));
            WriteGeneration(stateDirectory, newState, lockFile);
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

    private static Dictionary<string, DesiredFile> BuildDesired(IReadOnlyList<DeployedSei> sei)
    {
        var desired = new Dictionary<string, DesiredFile>(StringComparer.Ordinal);
        var caseInsensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sei)
        {
            foreach (var file in Directory.EnumerateFiles(item.Directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(item.Directory, file).Replace('\\', '/');
                if (relative is "sei.json" or "tooth.json")
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

                    existing.Owners.Add(item.Name);
                    continue;
                }

                desired[relative] = new DesiredFile
                {
                    SourcePath = file,
                    StoreDirectory = item.Directory,
                    Sha256 = hash,
                    Owners = [item.Name],
                    Modes = item.Manifest.Modes,
                };
            }
        }

        return desired;
    }

    private static HashSet<string> ValidateDesiredTree(
        string root,
        IReadOnlyDictionary<string, DesiredFile> desired,
        IReadOnlySet<string> managed)
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
                EnsureReplaceableDirectory(root, relative, destination, managed, desired);
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
        IReadOnlyDictionary<string, DesiredFile> desired)
    {
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

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                var child = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!managed.Contains(child) || desired.ContainsKey(child))
                {
                    throw new SeikoException(
                        $"path conflict: {relative} is a directory containing unmanaged file {child}",
                        "deploy");
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

    private static void WriteGeneration(string stateDirectory, LocalState state, SeikoLock lockFile)
    {
        var directory = Path.Combine(stateDirectory, "generations", state.Generation.ToString());
        Directory.CreateDirectory(directory);
        WriteAtomic(
            Path.Combine(directory, "state.json"),
            JsonSerializer.Serialize(state, SeikoJsonContext.Default.LocalState));
        LockFile.Write(Path.Combine(directory, "lock.json"), lockFile);
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
        => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Mutation(string RelativePath, DesiredFile? File);

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
        public Dictionary<string, string>? Modes { get; init; }
    }
}
