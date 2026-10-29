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
            throw new SeikoException($"source '{config.Id}': index not found: {IndexPath}", "source");
        }

        Index = JsonSerializer.Deserialize(File.ReadAllBytes(IndexPath), SeikoJsonContext.Default.IndexRoot)
            ?? throw new SeikoException($"source '{config.Id}': invalid index", "source");

        if (Index.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{config.Id}': unsupported index schemaVersion {Index.SchemaVersion}", "source");
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
            ?? throw new SeikoException($"source '{Config.Id}': invalid sei index for {name}", "source");

        if (sei.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{Config.Id}': unsupported sei schemaVersion {sei.SchemaVersion} for {name}", "source");
        }

        if (!string.Equals(sei.Name, name, StringComparison.Ordinal))
        {
            throw new SeikoException($"source '{Config.Id}': sei file for {name} declares name '{sei.Name}'", "source");
        }

        return sei;
    }

    public string ResolveArtifact(ArtifactLocation source) => ResolveArtifactPath(source);

    public async Task DownloadAsync(
        ArtifactLocation source,
        string directory,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveArtifactPath(source);
        var destination = Path.Combine(directory, "artifact");
        var statePath = Path.Combine(directory, "meta.json");
        var sourceInfo = new FileInfo(sourcePath);
        var size = sourceInfo.Length;
        var lastModified = sourceInfo.LastWriteTimeUtc.ToString("R");
        var state = PartialArtifactStateFile.Read(statePath);
        var reusable = state is not null
            && string.Equals(state.Url, sourcePath, StringComparison.Ordinal)
            && state.Size == size
            && string.Equals(state.LastModified, lastModified, StringComparison.Ordinal);
        if (!reusable)
        {
            PartialArtifactStateFile.ClearDirectory(directory);
            state = null;
        }

        state ??= new PartialArtifactState
        {
            Url = sourcePath,
            Size = size,
            LastModified = lastModified,
            SegmentCount = 1,
            SegmentSize = size,
        };
        PartialArtifactStateFile.Write(statePath, state);
        var have = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (have > size)
        {
            have = 0;
        }

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        input.Position = have;
        await using var output = new FileStream(
            destination,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        output.SetLength(have);
        output.Position = have;
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    public string ResolveArtifactPath(ArtifactLocation source)
    {
        if (source.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new SeikoException($"source '{Config.Id}': http artifacts are not supported in this slice: {source.Url}", "source");
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
