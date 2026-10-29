using System.Text.Json.Serialization;
using Seiko;

namespace Seiko.CommandLine;

public sealed record ListItem(
    string Name,
    string Version,
    string Source,
    bool Direct,
    Dictionary<string, string>? Selectors);

public sealed record SyncData(int Sei, int Files, string Mode);

public sealed record RollbackData(int Generation, int Sei, int Files);

public sealed record InitData(string Created);

public sealed record ErrorData(string Code, string Message);

public sealed record ListEnvelope(int SchemaVersion, string Command, List<ListItem> Data);

public sealed record SyncEnvelope(int SchemaVersion, string Command, SyncData Data);

public sealed record RollbackEnvelope(int SchemaVersion, string Command, RollbackData Data);

public sealed record InitEnvelope(int SchemaVersion, string Command, InitData Data);

public sealed record SearchEnvelope(int SchemaVersion, string Command, List<SeiSummary> Data);

public sealed record InfoVersion(
    string Version,
    bool Yanked,
    string? Deprecated,
    int Artifacts,
    List<SeiDependency> Dependencies);

public sealed record InfoData(string Name, string Source, List<InfoVersion> Versions);

public sealed record InfoEnvelope(int SchemaVersion, string Command, InfoData Data);

public sealed record WhyData(string Name, List<List<string>> Chains);

public sealed record WhyEnvelope(int SchemaVersion, string Command, WhyData Data);

public sealed record OutdatedEnvelope(int SchemaVersion, string Command, List<OutdatedSei> Data);

public sealed record SourceItem(string Id, string Index, int Priority);

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
[JsonSerializable(typeof(RollbackEnvelope))]
[JsonSerializable(typeof(InitEnvelope))]
[JsonSerializable(typeof(SearchEnvelope))]
[JsonSerializable(typeof(InfoEnvelope))]
[JsonSerializable(typeof(WhyEnvelope))]
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
[JsonSerializable(typeof(SeiDependency))]
[JsonSerializable(typeof(SeiSummary))]
[JsonSerializable(typeof(DoctorResult))]
[JsonSerializable(typeof(DoctorFinding))]
public partial class CliJsonContext : JsonSerializerContext
{
}
