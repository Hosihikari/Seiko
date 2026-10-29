namespace Seiko;

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

public sealed class IndexRoot
{
    public int SchemaVersion { get; set; } = 1;
    public string? GeneratedBy { get; set; }
    public List<string>? Sei { get; set; }
}

public sealed class SeiIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<SeiVersionEntry> Versions { get; set; } = [];
}

public sealed class SeiVersionEntry
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
    public ArtifactLocation Source { get; set; } = new();
}

public sealed class ArtifactLocation
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class SeikoConfig
{
    public List<SourceConfig> Sources { get; set; } = [];
    public string? Rid { get; set; }
    public List<SeiSpec> Sei { get; set; } = [];
    public List<SeiSpec> Components { get; set; } = [];
}

public sealed class SourceConfig
{
    public string Id { get; set; } = "";
    public string Index { get; set; } = "";
    public int Priority { get; set; }
    public string? TokenEnv { get; set; }
}

public sealed class SeiSpec
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class SeikoLock
{
    public int SchemaVersion { get; set; } = 1;
    public PlatformInfo Target { get; set; } = new();
    public List<string> Sources { get; set; } = [];
    public List<LockedSei> Sei { get; set; } = [];
}

public sealed class PlatformInfo
{
    public string Rid { get; set; } = "";
}

public sealed class LockedSei
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
    public Dictionary<string, DeployedFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DeployedFile
{
    public List<string> Owners { get; set; } = [];
    public string Sha256 { get; set; } = "";
}

public sealed class DeploymentJournal
{
    public int SchemaVersion { get; set; } = 1;
    public bool Committed { get; set; }
    public string? NewGenerationPath { get; set; }
    public List<DeploymentJournalEntry> Entries { get; set; } = [];
}

public sealed class DeploymentJournalEntry
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public int? UnixMode { get; set; }
}

public sealed class PartialArtifactState
{
    public int SchemaVersion { get; set; } = 1;
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int SegmentCount { get; set; } = 1;
    public long SegmentSize { get; set; }
}

public sealed record SeiSummary(string Name, string Version, string Source);

public sealed record SeiVersionInfo(
    string Version,
    bool Yanked,
    string? Deprecated,
    IReadOnlyList<SeiDependency> Dependencies,
    int ArtifactCount);

public sealed record SeiInfo(string Name, string Source, IReadOnlyList<SeiVersionInfo> Versions);
