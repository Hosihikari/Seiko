using System.Text.Json;

namespace Seiko;

public sealed class LocalSource : ISeiSource
{
    public SourceConfig Config { get; }
    public string Id => Config.Id;
    public string IndexPath { get; }
    public string BaseDirectory { get; }
    public IndexRoot Index { get; }

    public LocalSource(SourceConfig config, string root)
    {
        Config = config;
        IndexPath = ResolvePath(config.Index, root);
        BaseDirectory = Path.GetDirectoryName(IndexPath) ?? root;

        if (!File.Exists(IndexPath))
        {
            throw new SeikoException($"source '{config.Id}': index not found: {IndexPath}");
        }

        Index = JsonSerializer.Deserialize(File.ReadAllBytes(IndexPath), SeikoJsonContext.Default.IndexRoot)
            ?? throw new SeikoException($"source '{config.Id}': invalid index");

        if (Index.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{config.Id}': unsupported index schemaVersion {Index.SchemaVersion}");
        }
    }

    public SeiIndex? GetSei(string name)
    {
        var path = Path.Combine(BaseDirectory, "sei", name + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var sei = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.SeiIndex)
            ?? throw new SeikoException($"source '{Config.Id}': invalid sei index for {name}");

        if (sei.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{Config.Id}': unsupported sei schemaVersion {sei.SchemaVersion} for {name}");
        }

        if (!string.Equals(sei.Name, name, StringComparison.Ordinal))
        {
            throw new SeikoException($"source '{Config.Id}': sei file for {name} declares name '{sei.Name}'");
        }

        return sei;
    }

    public string ResolveArtifact(ArtifactLocation source) => ResolveArtifactPath(source);

    public void DownloadArtifact(ArtifactLocation source, string destination)
        => File.Copy(ResolveArtifactPath(source), destination, overwrite: true);

    public string ResolveArtifactPath(ArtifactLocation source)
    {
        if (source.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new SeikoException($"source '{Config.Id}': http artifacts are not supported in this slice: {source.Url}");
        }

        if (source.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(source.Url).LocalPath;
        }

        return Path.GetFullPath(Path.Combine(BaseDirectory, source.Url));
    }

    private static string ResolvePath(string value, string root)
    {
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(value).LocalPath;
        }

        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(root, value));
    }
}
