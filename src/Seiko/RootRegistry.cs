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
            var normalized = Path.GetFullPath(root);
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            if (state.Roots.Any(existing => comparer.Equals(existing, normalized)))
            {
                return;
            }

            state.Roots.Add(normalized);
            WriteState(storeRoot, state);
        }
    }

    public static IReadOnlyList<string> Read(string storeRoot)
        => ReadState(storeRoot)?.Roots ?? [];

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
