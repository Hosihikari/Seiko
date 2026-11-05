using System.Text.Json.Serialization;
using Seiko;

namespace Seiko.CommandLine;

public sealed record ListItem(
    string Name,
    string Version,
    string Source,
    bool Direct,
    Dictionary<string, string>? Selectors);

public sealed record SyncData(
    int Sei,
    int Files,
    string Mode,
    List<string> Warnings,
    List<PreviewDiagnostic>? Diagnostics);

public sealed record PreviewData(
    List<PlannedChange> Changes,
    List<PreviewBlock> Blocked,
    List<string> Warnings,
    bool Downloaded,
    List<PreviewDiagnostic>? Diagnostics);

public sealed record PreviewEnvelope(int SchemaVersion, string Command, PreviewData Data);

public sealed record RollbackData(int Generation, int Sei, int Files);

public sealed record InitData(string Created);

public sealed record ErrorData(string Code, string Message);

public sealed record ListEnvelope(int SchemaVersion, string Command, List<ListItem> Data);

public sealed record SyncEnvelope(int SchemaVersion, string Command, SyncData Data);

public sealed record RollbackEnvelope(int SchemaVersion, string Command, RollbackData Data);

public sealed record InitEnvelope(int SchemaVersion, string Command, InitData Data);

public sealed record SearchEnvelope(int SchemaVersion, string Command, List<SeiSummary> Data);

public sealed record InfoArtifact(
    string? Rid,
    List<SeiDependency> Dependencies,
    List<SeiDependency> Prerequisites,
    List<SeiDependency> OptionalPeers,
    List<SeiDependency> Conflicts,
    int External,
    List<string> ExternalHosts);

public sealed record InfoVersion(
    string Version,
    bool Yanked,
    string? Deprecated,
    AdvisoryInfo? Advisory,
    int Artifacts,
    List<SeiDependency> Dependencies,
    List<SeiDependency> Prerequisites,
    List<SeiDependency> OptionalPeers,
    List<SeiDependency> Conflicts,
    List<InfoArtifact> ArtifactDetails,
    bool SourceRetired);

public sealed record InfoData(
    string Name,
    string Source,
    bool Unlisted,
    string? Deprecated,
    List<InfoVersion> Versions,
    ProjectMetadata? Project,
    string? UpstreamHost,
    bool MaintainedHere);

public sealed record InfoEnvelope(int SchemaVersion, string Command, InfoData Data);

public sealed record WhyData(string Name, List<List<WhyLink>> Chains);

public sealed record WhyEnvelope(int SchemaVersion, string Command, WhyData Data);

public sealed record OutdatedEnvelope(int SchemaVersion, string Command, List<OutdatedSei> Data);

public sealed record SourceItem(string Id, string Index, List<string>? TokenHosts);

public sealed record SourceEnvelope(int SchemaVersion, string Command, List<SourceItem> Data);

public sealed record ConfigData(string? Rid);

public sealed record ConfigEnvelope(int SchemaVersion, string Command, ConfigData Data);

public sealed record CacheStatusEnvelope(int SchemaVersion, string Command, CacheStatusInfo Data);

public sealed record CacheVerifyEnvelope(int SchemaVersion, string Command, List<CacheFinding> Data);

public sealed record CacheCleanEnvelope(int SchemaVersion, string Command, CacheCleanResult Data);

public sealed record DoctorEnvelope(int SchemaVersion, string Command, DoctorResult Data);

public sealed record IndexVerifyEnvelope(int SchemaVersion, string Command, IndexVerifyResult Data);

public sealed record ErrorEnvelope(int SchemaVersion, string Command, ErrorData Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ListEnvelope))]
[JsonSerializable(typeof(SyncEnvelope))]
[JsonSerializable(typeof(PreviewEnvelope))]
[JsonSerializable(typeof(PreviewData))]
[JsonSerializable(typeof(PlannedChange))]
[JsonSerializable(typeof(PreviewBlock))]
[JsonSerializable(typeof(PreviewDiagnostic))]
[JsonSerializable(typeof(List<PlannedChange>))]
[JsonSerializable(typeof(List<PreviewBlock>))]
[JsonSerializable(typeof(RollbackEnvelope))]
[JsonSerializable(typeof(InitEnvelope))]
[JsonSerializable(typeof(SearchEnvelope))]
[JsonSerializable(typeof(InfoEnvelope))]
[JsonSerializable(typeof(WhyEnvelope))]
[JsonSerializable(typeof(WhyLink))]
[JsonSerializable(typeof(OutdatedEnvelope))]
[JsonSerializable(typeof(OutdatedSei))]
[JsonSerializable(typeof(SourceEnvelope))]
[JsonSerializable(typeof(SourceItem))]
[JsonSerializable(typeof(ConfigEnvelope))]
[JsonSerializable(typeof(ConfigData))]
[JsonSerializable(typeof(CacheStatusEnvelope))]
[JsonSerializable(typeof(CacheStatusInfo))]
[JsonSerializable(typeof(CacheVerifyEnvelope))]
[JsonSerializable(typeof(CacheFinding))]
[JsonSerializable(typeof(CacheCleanEnvelope))]
[JsonSerializable(typeof(CacheCleanResult))]
[JsonSerializable(typeof(DoctorEnvelope))]
[JsonSerializable(typeof(IndexVerifyEnvelope))]
[JsonSerializable(typeof(IndexVerifyResult))]
[JsonSerializable(typeof(IndexFinding))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(InfoData))]
[JsonSerializable(typeof(InfoVersion))]
[JsonSerializable(typeof(InfoArtifact))]
[JsonSerializable(typeof(SeiDependency))]
[JsonSerializable(typeof(SeiSummary))]
[JsonSerializable(typeof(DoctorResult))]
[JsonSerializable(typeof(DoctorFinding))]
public partial class CliJsonContext : JsonSerializerContext
{
}
