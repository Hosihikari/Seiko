namespace Seiko.Core;

public interface IPackageSource
{
    string Id { get; }
    SourceIndex Index { get; }
    PackageIndex? GetPackage(string name);
    string ResolveArtifact(ArtifactSource source);
    void DownloadArtifact(ArtifactSource source, string destination);
}
