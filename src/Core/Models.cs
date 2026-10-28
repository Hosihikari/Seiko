namespace Seiko.Core;

public sealed class SeiManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<SeiDependency> Dependencies { get; set; } = [];
    public Dictionary<string, string>? Selectors { get; set; }
    public string? Layout { get; set; }
    public Dictionary<string, string>? Modes { get; set; }
}

public sealed class SeiDependency
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class SourceIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string? GeneratedBy { get; set; }
    public List<string>? Packages { get; set; }
}

public sealed class PackageIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<PackageVersionEntry> Versions { get; set; } = [];
}

public sealed class PackageVersionEntry
{
    public string Version { get; set; } = "";
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<ArtifactEntry> Artifacts { get; set; } = [];
    public bool Yanked { get; set; }
    public string? Deprecated { get; set; }
}

public sealed class ArtifactEntry
{
    public Dictionary<string, string>? Selectors { get; set; }
    public string? Layout { get; set; }
    public ArtifactSource Source { get; set; } = new();
}

public sealed class ArtifactSource
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class SeikoConfig
{
    public List<SourceConfig> Sources { get; set; } = [];
    public string? Rid { get; set; }
    public List<PackageSpec> Packages { get; set; } = [];
    public List<PackageSpec> Components { get; set; } = [];
}

public sealed class SourceConfig
{
    public string Id { get; set; } = "";
    public string Index { get; set; } = "";
    public int Priority { get; set; }
    public string? TokenEnv { get; set; }
}

public sealed class PackageSpec
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class SeikoLock
{
    public int SchemaVersion { get; set; } = 1;
    public TargetInfo Target { get; set; } = new();
    public List<string> Sources { get; set; } = [];
    public List<LockedPackage> Packages { get; set; } = [];
}

public sealed class TargetInfo
{
    public string Rid { get; set; } = "";
}

public sealed class LockedPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Direct { get; set; }
    public LockedArtifact Artifact { get; set; } = new();
    public List<SeiDependency> Dependencies { get; set; } = [];
}

public sealed class LockedArtifact
{
    public string Sha256 { get; set; } = "";
    public Dictionary<string, string>? Selectors { get; set; }
}

public sealed class LocalState
{
    public int SchemaVersion { get; set; } = 1;
    public int Generation { get; set; }
    public Dictionary<string, ProjectedFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProjectedFile
{
    public List<string> Owners { get; set; } = [];
    public string Sha256 { get; set; } = "";
}
