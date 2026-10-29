namespace Seiko;

public interface ISeiSource
{
    string Id { get; }
    IndexRoot Index { get; }
    SeiIndex? GetSei(string name);
    string ResolveArtifact(ArtifactLocation source);
    Task DownloadAsync(
        string name,
        ArtifactLocation source,
        string directory,
        DownloadSession session,
        CancellationToken cancellationToken);
}
