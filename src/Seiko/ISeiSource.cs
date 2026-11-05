namespace Seiko;

public interface ISeiSource
{
    string Id { get; }
    IndexRoot Index { get; }
    SeiIndex? GetSei(string name);

    // Null means the source has no usable catalog; callers fall back to the
    // per-name path and never treat a catalog as authoritative.
    SeiCatalog? Catalog { get; }

    string ResolveArtifact(ArtifactLocation source);
    Task DownloadAsync(
        string name,
        ArtifactLocation source,
        string directory,
        DownloadSession session,
        CancellationToken cancellationToken);
}
