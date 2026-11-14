namespace Seiko;

public sealed class SeiManifest
{
    // 3 adds the external file/archive declarations; 4 adds `preserve`. A
    // manifest without them stays at the older version; clients that predate
    // a version reject it at their exact-version check instead of silently
    // installing a metadata-only zip as an empty package.
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
    public ArtifactManifest? Artifact { get; set; }
}

public sealed class ArtifactManifest
{
    public string? Rid { get; set; }
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
    public string? Layout { get; set; }
    public Dictionary<string, string>? Modes { get; set; }

    // Exact paths this artifact deploys but does not own afterwards: an
    // existing file wins over the artifact's copy, and the path is never
    // recorded as managed. Schema 4 only.
    public List<string>? Preserve { get; set; }

    // Mutually exclusive candidate relation sets (ADR-0045). At most one is
    // expanded for the selected artifact; the artifact-level relations above
    // are common constraints shared by every group.
    public List<DependencyGroup>? DependencyGroups { get; set; }
    public List<ExternalFile>? ExternalFiles { get; set; }
    public List<ExternalArchive>? ExternalArchives { get; set; }
}

// One candidate relation set inside an artifact. Same four kinds, no own
// rid, modes, preserve or external payload (ADR-0045).
public sealed class DependencyGroup
{
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
}

// A single upstream byte source mapped to one path inside the package. The
// digest is the authority; the URL is only a location.
public sealed class ExternalFile
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

// One archive download can feed several target paths. Only the declared
// members are extracted and the archive digest covers all of them.
public sealed class ExternalArchive
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Format { get; set; } = "";
    public long Size { get; set; }
    public List<ExternalArchiveMember> Members { get; set; } = [];
}

public sealed class ExternalArchiveMember
{
    public string Member { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class SeiDependency
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class IndexRoot
{
    public const int CurrentSchemaVersion = 2;

    // A local source may be configured as its directory (design §7); the
    // index file always has this name inside it.
    public const string FileName = "index.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? GeneratedBy { get; set; }
    public List<string>? Sei { get; set; }
}

public sealed class SeiIndex
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public bool Unlisted { get; set; }
    public string? Deprecated { get; set; }

    // Maintainer-published project metadata. Ignored by old clients; only
    // shown by newer ones. The manifest stays the identity source.
    public ProjectMetadata? Project { get; set; }

    // Index-side bookkeeping. Clients ignore both; `audit`/`info` may show
    // the origin of a retirement state.
    public UpstreamInfo? Upstream { get; set; }
    public List<string>? Maintainers { get; set; }

    // Index-side overlay, applied on top of the maintainer's fields.
    public bool? SourceUnlisted { get; set; }
    public string? SourceDeprecated { get; set; }

    // Origin mirrors (ADR-0044). Optional and derived: the declaration is the
    // authority, these only let `info` and `--dry-run` show the location
    // without reading the manifest.
    public List<string>? OriginPaths { get; set; }
    public List<OriginSelector>? OriginSelectors { get; set; }

    public List<SeiVersionEntry> Versions { get; set; } = [];

    // Only ever set in memory by the offline probe: an earlier source could
    // not be read, so the entries here may not be what the real run chooses.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProbeShadowed { get; set; }
}

public sealed class ProjectMetadata
{
    public string Description { get; set; } = "";
    public List<string> Authors { get; set; } = [];
    public string License { get; set; } = "";
    public List<string>? Tags { get; set; }
    public string? ProjectUrl { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? RepositoryType { get; set; }
}

public sealed class UpstreamInfo
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class SeiVersionEntry
{
    public string Version { get; set; } = "";
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
    public List<ArtifactEntry> Artifacts { get; set; } = [];
    public bool Yanked { get; set; }
    public string? Deprecated { get; set; }
    public AdvisoryInfo? Advisory { get; set; }
    public bool? SourceYanked { get; set; }
    public List<AdvisoryInfo>? SourceAdvisories { get; set; }

    // Only ever set in memory by the offline probe: the version is invented
    // for a name whose cached metadata is absent, so range, admission, peer
    // and conflict checks must not judge it. See OfflineMetadata.ProbeSource.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ProbeUnknown { get; set; }
}

public sealed class AdvisoryInfo
{
    public string Severity { get; set; } = "";
    public string? Url { get; set; }
}

public sealed class ArtifactEntry
{
    public string? Rid { get; set; }
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
    public string? Layout { get; set; }
    public Dictionary<string, string>? Modes { get; set; }

    // Mirrored from the manifest for previews, which do not open artifacts.
    public List<string>? Preserve { get; set; }
    public List<DependencyGroup>? DependencyGroups { get; set; }
    public List<ExternalFile>? ExternalFiles { get; set; }
    public List<ExternalArchive>? ExternalArchives { get; set; }

    // Mirrored by the index for `info` and `--dry-run` so neither has to open
    // the artifact. The manifest stays the authority; CI compares the two.
    public ExternalSummary? External { get; set; }

    // Origin artifact: the stored path, relative to the install root.
    public string? OriginPath { get; set; }

    public ArtifactLocation Source { get; set; } = new();
}

public sealed class ExternalSummary
{
    public int Files { get; set; }
    public int Archives { get; set; }
    public long Size { get; set; }
    public List<string> Hosts { get; set; } = [];
}

public sealed class ArtifactLocation
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class SeikoConfig
{
    public const int CurrentSchemaVersion = 2;

    public List<SourceConfig> Sources { get; set; } = [];
    public string? Rid { get; set; }
    public List<SeiSpec> Sei { get; set; } = [];
    public List<OriginSpec> Origin { get; set; } = [];
}

// A direct declaration served by a local .sei file instead of a source
// (ADR-0044). Paths are relative to the install root and parallel to
// Selectors; one entry answers one name, one version, one artifact.
public sealed class OriginSpec
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = [];
    public List<OriginSelector> Selectors { get; set; } = [];
}

// The condition a path is chosen under, matching an artifact selector.
public sealed class OriginSelector
{
    public string? Rid { get; set; }
    public List<SeiDependency> Prerequisites { get; set; } = [];
}

public sealed class SourceConfig
{
    public string Id { get; set; } = "";
    public string Index { get; set; } = "";
    public List<string>? TokenHosts { get; set; }
}

public sealed class SeiSpec
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class SeikoLock
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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

    // Set only for `source = "origin"`: the stored path, relative to the
    // install root, the bytes are read from (ADR-0044).
    public string? OriginPath { get; set; }
    public bool Direct { get; set; }
    public LockedArtifact Artifact { get; set; } = new();
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
}

public sealed class LockedArtifact
{
    public string Sha256 { get; set; } = "";
    public string? Rid { get; set; }

    // The selected dependency group, by position in the artifact's list; null
    // when the artifact declares none. The relation lists below already hold
    // the merged result, so replay needs no merging (ADR-0045).
    public int? Group { get; set; }
    public List<SeiDependency> Dependencies { get; set; } = [];
    public List<SeiDependency> Prerequisites { get; set; } = [];
    public List<SeiDependency> OptionalPeers { get; set; } = [];
    public List<SeiDependency> Conflicts { get; set; } = [];
}

public sealed class LocalState
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int Generation { get; set; }
    public Dictionary<string, DeployedFile> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DeployedFile
{
    public List<string> Owners { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public bool Adopted { get; set; }

    // The path is a preserve target that --reset-preserved turned into a
    // managed file. It keeps being managed on later runs even while the
    // artifact still declares it; his flag is what distinguishes the user's
    // opt-in from a path an earlier version managed and a later one declared
    // preserve (ADR-0045).
    public bool Preserved { get; set; }
}

// Generation snapshots have no timestamp of their own; this sidecar records
// one so `generation list` can order them. Old generations simply lack it.
public sealed class GenerationMeta
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

// One row of `generation list`. Valid is false when the snapshot cannot be
// rolled back; the row still appears so the gap is visible.
public sealed record GenerationInfo(
    int Generation,
    DateTimeOffset? CreatedAt,
    bool Current,
    int SeiCount,
    int FileCount,
    bool Valid);

// The read-only diff `rollback --dry-run` prints. It never changes state and
// never downloads; a missing store entry is reported, not fetched.
public sealed record RollbackPreview(
    int FromGeneration,
    int ToGeneration,
    IReadOnlyList<string> ConfigChanges,
    IReadOnlyList<string> LockChanges,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> MissingStore);

// One audit finding. `fix` is the newest version without that advisory, or
// null when none is known; `unknown` marks a package the audit could not read.
public sealed record AuditFinding(
    string Name,
    string Version,
    string Kind,
    string? Severity,
    string? Url,
    string? Fix);

public sealed record AuditResult(
    IReadOnlyList<AuditFinding> Findings,
    IReadOnlyList<string> Unknown);

// One row of `source status`. Catalog issues are warnings, index issues are
// errors; nothing here changes a source or falls back to another one.
public sealed record SourceStatus(
    string Id,
    bool Reachable,
    int? SeiCount,
    bool CatalogPresent,
    IReadOnlyList<string> Problems);

public sealed class RootRegistryState
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Roots { get; set; } = [];
}

public sealed class DeploymentJournal
{
    public int SchemaVersion { get; set; } = 1;
    public bool Committed { get; set; }
    public string? NewGenerationPath { get; set; }
    public List<string>? TemporaryPaths { get; set; }
    public List<string>? TrashPaths { get; set; }
    public List<DeploymentJournalEntry> Entries { get; set; } = [];
}

public sealed class DeploymentJournalEntry
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? Sha256 { get; set; }
    public int? UnixMode { get; set; }
}

public sealed class PartialArtifactState
{
    public int SchemaVersion { get; set; } = 2;
    public string UrlHash { get; set; } = "";
    public long Size { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int SegmentCount { get; set; } = 1;
    public long SegmentSize { get; set; }
}

public sealed record SeiSummary(
    string Name,
    string Version,
    string Source,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? License = null,
    string? Deprecated = null,
    AdvisoryInfo? Advisory = null);

// A link of a `why` chain; the kind names the relation that made the edge.
public sealed record WhyLink(string Name, string Kind);

public sealed record SeiArtifactInfo(
    string? Rid,
    IReadOnlyList<SeiDependency> Dependencies,
    IReadOnlyList<SeiDependency> Prerequisites,
    IReadOnlyList<SeiDependency> OptionalPeers,
    IReadOnlyList<SeiDependency> Conflicts,
    int ExternalCount,
    IReadOnlyList<string> ExternalHosts,
    bool ExternalKnown = true);

public sealed record SeiVersionInfo(
    string Version,
    bool Yanked,
    string? Deprecated,
    AdvisoryInfo? Advisory,
    IReadOnlyList<SeiDependency> Dependencies,
    IReadOnlyList<SeiDependency> Prerequisites,
    IReadOnlyList<SeiDependency> OptionalPeers,
    IReadOnlyList<SeiDependency> Conflicts,
    IReadOnlyList<SeiArtifactInfo> Artifacts,
    bool SourceRetired = false)
{
    public int ArtifactCount => Artifacts.Count;
}

public sealed record SeiInfo(
    string Name,
    string Source,
    bool Unlisted,
    string? Deprecated,
    IReadOnlyList<SeiVersionInfo> Versions,
    ProjectMetadata? Project = null,
    string? UpstreamHost = null,
    bool MaintainedHere = false,
    // Origin entries carry no index cross-check (ADR-0044).
    bool IndexVerified = true,
    IReadOnlyList<string>? OriginPaths = null);
