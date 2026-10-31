using System.Text;
using System.Text.Json;

namespace Seiko;

internal static class RootRegistry
{
    private const string RegistryName = "roots.json";
    private const string LockName = "roots.lock";

    public static void Register(string storeRoot, string root)
    {
        try
        {
            Directory.CreateDirectory(storeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SeikoException(
                $"could not register install root for cache cleanup: {ex.Message}",
                "io",
                ex);
        }

        var lockPath = Path.Combine(storeRoot, LockName);
        ProcessLock? registryLock = null;
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5 && registryLock is null; attempt++)
        {
            try
            {
                registryLock = ProcessLock.Acquire(lockPath, "store root registry", "io");
            }
            catch (SeikoException ex)
            {
                lastError = ex;
                Thread.Sleep(100 * (attempt + 1));
            }
        }

        if (registryLock is null)
        {
            var message = $"could not register install root for cache cleanup: {lastError?.Message}";
            throw lastError is null
                ? new SeikoException(message, "io")
                : new SeikoException(message, "io", lastError);
        }

        using (registryLock)
        {
            var state = ReadState(storeRoot) ?? new RootRegistryState();
            var normalized = Resolve(root);
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var roots = state.Roots.Select(Resolve).Distinct(comparer).ToList();
            if (!roots.Contains(normalized, comparer))
            {
                roots.Add(normalized);
            }

            if (!roots.SequenceEqual(state.Roots, comparer))
            {
                state.Roots = roots;
                WriteState(storeRoot, state);
            }
        }
    }

    public static IReadOnlyList<string> Read(string storeRoot)
    {
        var state = ReadState(storeRoot);
        if (state is null)
        {
            return [];
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return state.Roots.Select(Resolve).Distinct(comparer).ToList();
    }

    // Resolve every symlink or junction component so two paths to the same
    // directory register once and cache clean does not lock the same root twice.
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return full;
            }

            var current = root;
            foreach (var segment in full[root.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                current = MatchEntryCase(current, segment);
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    current = Path.GetFullPath(target.FullName);
                }
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return full;
        }
    }

    // On case-insensitive volumes a path spelled with different case still
    // exists; adopt the directory entry's actual spelling so two aliases of one
    // directory compare equal. On case-sensitive volumes a differently-cased
    // path does not exist and is left alone, keeping distinct roots distinct.
    private static string MatchEntryCase(string candidate, string segment)
    {
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        var parent = Path.GetDirectoryName(candidate);
        if (string.IsNullOrEmpty(parent))
        {
            return candidate;
        }

        try
        {
            var entries = Directory.EnumerateFileSystemEntries(parent)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .ToList();
            if (entries.Any(name => string.Equals(name, segment, StringComparison.Ordinal)))
            {
                return candidate;
            }

            var matches = entries
                .Where(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matches.Count == 1 ? Path.Combine(parent, matches[0]!) : candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return candidate;
        }
    }

    private static RootRegistryState? ReadState(string storeRoot)
    {
        var path = Path.Combine(storeRoot, RegistryName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.RootRegistryState);
            if (state is null || state.SchemaVersion != 1)
            {
                throw new SeikoException($"invalid root registry: {path}", "io");
            }

            return state;
        }
        catch (JsonException ex)
        {
            throw new SeikoException($"invalid root registry: {path}: {ex.Message}", "io", ex);
        }
    }

    private static void WriteState(string storeRoot, RootRegistryState state)
    {
        var path = Path.Combine(storeRoot, RegistryName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(state, SeikoJsonContext.Default.RootRegistryState),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
