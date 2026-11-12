using System.Text.Json.Serialization;

namespace Seiko;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    // A typo in a declared field would otherwise be dropped without a trace:
    // `prerequisits` verified clean because nothing read it (ADR-0036 reports
    // unknown fields; this makes deserialization say so instead of guessing).
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SeiManifest))]
[JsonSerializable(typeof(ArtifactManifest))]
[JsonSerializable(typeof(ExternalFile))]
[JsonSerializable(typeof(ExternalArchive))]
[JsonSerializable(typeof(ExternalArchiveMember))]
[JsonSerializable(typeof(IndexRoot))]
[JsonSerializable(typeof(SeiIndex))]
[JsonSerializable(typeof(SeiCatalog))]
[JsonSerializable(typeof(SeikoLock))]
[JsonSerializable(typeof(LocalState))]
[JsonSerializable(typeof(PartialArtifactState))]
[JsonSerializable(typeof(DeploymentJournal))]
[JsonSerializable(typeof(RootRegistryState))]
[JsonSerializable(typeof(GenerationMeta))]
[JsonSerializable(typeof(StoreDigestsState))]
public partial class SeikoJsonContext : JsonSerializerContext
{
}
