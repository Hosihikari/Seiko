using System.Text.Json.Serialization;

namespace Seiko;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SeiManifest))]
[JsonSerializable(typeof(IndexRoot))]
[JsonSerializable(typeof(SeiIndex))]
[JsonSerializable(typeof(SeikoLock))]
[JsonSerializable(typeof(LocalState))]
[JsonSerializable(typeof(PartialArtifactState))]
[JsonSerializable(typeof(DeploymentJournal))]
public partial class SeikoJsonContext : JsonSerializerContext
{
}
