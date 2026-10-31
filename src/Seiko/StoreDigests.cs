using System.Text;
using System.Text.Json;

namespace Seiko;

public sealed class StoreDigestsState
{
    public int SchemaVersion { get; set; } = 1;
    public string Artifact { get; set; } = "";
    public List<StoreFileDigest> Files { get; set; } = [];
}

public sealed class StoreFileDigest
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

// Per-file digests for one extracted artifact. They live next to the artifact
// directories in .meta so a vulnerable store never deploys altered payloads
// and `sk cache verify` can check every file, not just sei.json.
internal static class StoreDigests
{
    public const string DirectoryName = ".meta";

    public static string PathFor(string storeRoot, string artifact)
        => Path.Combine(storeRoot, DirectoryName, artifact + ".json");

    public static StoreDigestsState? TryRead(string storeRoot, string artifact)
    {
        var path = PathFor(storeRoot, artifact);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.StoreDigestsState);
            if (state is not { SchemaVersion: 1 }
                || !string.Equals(state.Artifact, artifact, StringComparison.Ordinal)
                || state.Files is null)
            {
                return null;
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in state.Files)
            {
                if (file is null
                    || !IsSafeRelativePath(file.Path)
                    || !Hashing.IsSha256(file.Sha256)
                    || file.Size < 0
                    || !paths.Add(file.Path))
                {
                    return null;
                }
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Contains('\0')
            || path.Contains('\\')
            || Path.IsPathRooted(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows()
            && path.IndexOfAny(Path.GetInvalidFileNameChars()
                .Where(character => character is not ('/' or '\\'))
                .ToArray()) >= 0)
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    public static void Write(string storeRoot, StoreDigestsState state)
    {
        Directory.CreateDirectory(Path.Combine(storeRoot, DirectoryName));
        var path = PathFor(storeRoot, state.Artifact);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(state, SeikoJsonContext.Default.StoreDigestsState),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    public static StoreDigestsState Compute(string directory, string artifact)
    {
        var files = new List<StoreFileDigest>();
        foreach (var file in Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            files.Add(new StoreFileDigest
            {
                Path = Path.GetRelativePath(directory, file).Replace('\\', '/'),
                Sha256 = Hashing.Sha256File(file),
                Size = new FileInfo(file).Length,
            });
        }

        return new StoreDigestsState { Artifact = artifact, Files = files };
    }

    public static void TryDelete(string storeRoot, string artifact)
    {
        try
        {
            var path = PathFor(storeRoot, artifact);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void CleanOrphans(string storeRoot)
    {
        var directory = Path.Combine(storeRoot, DirectoryName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var artifact = Path.GetFileNameWithoutExtension(path);
            if (artifact.Length == 64 && artifact.All(Uri.IsHexDigit))
            {
                if (Directory.Exists(Path.Combine(storeRoot, artifact)))
                {
                    continue;
                }
            }

            try
            {
                File.Delete(path);
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
