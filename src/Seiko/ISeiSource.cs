namespace Seiko;

public interface ISeiSource
{
    string Id { get; }
    IndexRoot Index { get; }
    SeiIndex? GetPackage(string name);
    string ResolveArtifact(ArtifactLocation source);
    void DownloadArtifact(ArtifactLocation source, string destination);
}
