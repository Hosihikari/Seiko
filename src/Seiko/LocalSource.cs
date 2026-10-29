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

    public SeiIndex? GetPackage(string name)
    {
        var path = Path.Combine(BaseDirectory, "packages", name + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        var package = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.SeiIndex)
            ?? throw new SeikoException($"source '{Config.Id}': invalid package index for {name}");

        if (package.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{Config.Id}': unsupported package schemaVersion {package.SchemaVersion} for {name}");
        }

        if (!string.Equals(package.Name, name, StringComparison.Ordinal))
        {
            throw new SeikoException($"source '{Config.Id}': package file for {name} declares name '{package.Name}'");
        }

        return package;
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
