using System.Text.Json;

namespace Seiko;

internal static class PartialArtifactStateFile
{
    public static PartialArtifactState? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.PartialArtifactState);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write(string path, PartialArtifactState state)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(
            temp,
            JsonSerializer.SerializeToUtf8Bytes(state, SeikoJsonContext.Default.PartialArtifactState));
        File.Move(temp, path, overwrite: true);
    }

    public static bool HasValidator(PartialArtifactState state)
        => !string.IsNullOrWhiteSpace(state.ETag) || !string.IsNullOrWhiteSpace(state.LastModified);

    public static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            Directory.Delete(child, recursive: true);
        }
    }
}
