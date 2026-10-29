namespace Seiko;

public interface ISeiSource
{
    string Id { get; }
    IndexRoot Index { get; }
    SeiIndex? GetSei(string name);
    string ResolveArtifact(ArtifactLocation source);
    void DownloadArtifact(ArtifactLocation source, string destination);
}
