using System.Text.Json;

namespace Seiko;

// Index metadata fetched over HTTP is cached so offline replays and previews
// do not have to touch the network. The cache is keyed by the absolute URL;
// an entry is only read when it parses and matches the expected schema.
internal static class MetadataCache
{
    public const string DirectoryName = ".metadata";

    public static string PathFor(string storeRoot, string url)
        => Path.Combine(storeRoot, DirectoryName, Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(url)) + ".json");

    // A source that answered 404 for a name is worth remembering: without it
    // an offline replay fails on the first source that simply lacks the sei,
    // even though another source supplied it online. The two answers are
    // mutually exclusive so only the most recent one can be read back.
    private static string MissingPath(string path) => path + ".404";

    public static void WriteMissing(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(MissingPath(path), "");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static bool IsMissing(string path) => File.Exists(MissingPath(path));

    public static void ClearMissing(string path)
    {
        var marker = MissingPath(path);
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }
    }

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, info));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public static T? TryRead<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), info);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

}
