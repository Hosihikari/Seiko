using System.Text;
using System.Text.Json;

namespace Seiko.Core;

public sealed record ProjectionPackage(string Name, string Version, string Directory, SeiManifest Manifest);

public sealed class ProjectionResult
{
    public required int FileCount { get; init; }
}

public static class Projector
{
    public static ProjectionResult Apply(
        string root,
        IReadOnlyList<ProjectionPackage> packages,
        string stateDirectory,
        SeikoLock lockFile,
        int? generationOverride = null)
    {
        var desired = new Dictionary<string, DesiredFile>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            foreach (var file in Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(package.Directory, file).Replace('\\', '/');
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
                            $"file conflict: {relative} differs between {existing.Owners[0]} and {package.Name}");
                    }

                    existing.Owners.Add(package.Name);
                    continue;
                }

                desired[relative] = new DesiredFile
                {
                    SourcePath = file,
                    Sha256 = hash,
                    Owners = [package.Name],
                    Modes = package.Manifest.Modes,
                };
            }
        }

        var statePath = Path.Combine(stateDirectory, "state.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(statePath), SeikoJsonContext.Default.LocalState) ?? new LocalState()
            : new LocalState();

        foreach (var oldFile in state.Files.Keys)
        {
            if (desired.ContainsKey(oldFile))
            {
                continue;
            }

            var fullPath = Native(root, oldFile);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        foreach (var (relative, file) in desired)
        {
            var destination = Native(root, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var unchanged = File.Exists(destination)
                && string.Equals(Hashing.Sha256File(destination), file.Sha256, StringComparison.Ordinal);

            if (!unchanged)
            {
                // Shortcut: copy projection; hardlink/reflink is a later optimization.
                var temp = destination + ".seiko-tmp-" + Guid.NewGuid().ToString("N");
                File.Copy(file.SourcePath, temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
            }

            ApplyMode(destination, relative, file.Modes);
        }

        Directory.CreateDirectory(stateDirectory);
        var newState = new LocalState
        {
            Generation = generationOverride ?? state.Generation + 1,
            Files = desired.ToDictionary(
                pair => pair.Key,
                pair => new ProjectedFile
                {
                    Owners = pair.Value.Owners.Distinct(StringComparer.Ordinal).OrderBy(o => o, StringComparer.Ordinal).ToList(),
                    Sha256 = pair.Value.Sha256,
                },
                StringComparer.Ordinal),
        };

        WriteAtomic(statePath, JsonSerializer.Serialize(newState, SeikoJsonContext.Default.LocalState));
        WriteGeneration(stateDirectory, newState, lockFile);
        WriteInstalledManifests(stateDirectory, packages);
        return new ProjectionResult { FileCount = desired.Count };
    }

    private static void WriteGeneration(string stateDirectory, LocalState state, SeikoLock lockFile)
    {
        var generations = Path.Combine(stateDirectory, "generations");
        var directory = Path.Combine(generations, state.Generation.ToString());
        Directory.CreateDirectory(directory);
        WriteAtomic(
            Path.Combine(directory, "state.json"),
            JsonSerializer.Serialize(state, SeikoJsonContext.Default.LocalState));
        LockFile.Write(Path.Combine(directory, "lock.json"), lockFile);

        foreach (var path in Directory.EnumerateDirectories(generations))
        {
            var name = Path.GetFileName(path);
            if (int.TryParse(name, out var generation) && generation <= state.Generation - 3)
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static void WriteInstalledManifests(string stateDirectory, IReadOnlyList<ProjectionPackage> packages)
    {
        foreach (var package in packages)
        {
            var directory = Path.Combine(stateDirectory, "installed", package.Name, package.Version);
            Directory.CreateDirectory(directory);
            File.Copy(
                Path.Combine(package.Directory, "sei.json"),
                Path.Combine(directory, "sei.json"),
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
            throw new SeikoException($"invalid mode '{mode}' for {relative}", ex);
        }
    }

    private static string Native(string root, string relative)
        => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private sealed class DesiredFile
    {
        public required string SourcePath { get; init; }
        public required string Sha256 { get; init; }
        public required List<string> Owners { get; init; }
        public Dictionary<string, string>? Modes { get; init; }
    }
}
