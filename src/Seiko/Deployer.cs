using System.Text;
using System.Text.Json;

namespace Seiko;

public sealed record DeployedSei(string Name, string Version, string Directory, SeiManifest Manifest);

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

        var mutations = new List<Mutation>();
        foreach (var oldFile in state.Files.Keys)
        {
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
        try
        {
            transaction = DeploymentTransaction.Begin(root, stateDirectory);
            foreach (var mutation in mutations)
            {
                transaction.WillChange(mutation.RelativePath);
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
                    Directory.CreateDirectory(parent);
                }

                // Shortcut: copy deployment; hardlink/reflink is a later optimization.
                var temp = destination + ".seiko-tmp-" + Guid.NewGuid().ToString("N");
                File.Copy(mutation.File.SourcePath, temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
                ApplyMode(destination, mutation.RelativePath, mutation.File.Modes);
                AppliedForTests.Value?.Invoke(++applied);
            }

            foreach (var (relative, file) in desired)
            {
                ApplyMode(Native(root, relative), relative, file.Modes);
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
            if (transaction is not null)
            {
                transaction.RollbackAfterFailure(ex);
            }

            throw;
        }

        PruneGenerations(stateDirectory, newState.Generation);
        return new DeployResult { FileCount = desired.Count };
    }

    private static Dictionary<string, DesiredFile> BuildDesired(IReadOnlyList<DeployedSei> sei)
    {
        var desired = new Dictionary<string, DesiredFile>(StringComparer.Ordinal);
        foreach (var item in sei)
        {
            foreach (var file in Directory.EnumerateFiles(item.Directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(item.Directory, file).Replace('\\', '/');
                if (relative is "sei.json" or "tooth.json")
                {
                    continue;
                }

                var hash = Hashing.Sha256File(file);
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
                    Sha256 = hash,
                    Owners = [item.Name],
                    Modes = item.Manifest.Modes,
                };
            }
        }

        return desired;
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

    private static void ApplyMode(string path, string relative, Dictionary<string, string>? modes)
    {
        if (OperatingSystem.IsWindows() || modes is null || !modes.TryGetValue(relative, out var mode))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(mode, 8));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new SeikoException($"invalid mode '{mode}' for {relative}", "deploy", ex);
        }
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

    private sealed class DesiredFile
    {
        public required string SourcePath { get; init; }
        public required string Sha256 { get; init; }
        public required List<string> Owners { get; init; }
        public Dictionary<string, string>? Modes { get; init; }
    }
}
