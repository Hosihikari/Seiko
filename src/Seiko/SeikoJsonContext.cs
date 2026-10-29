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
public partial class SeikoJsonContext : JsonSerializerContext
{
}
