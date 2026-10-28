using System.Text.Json.Serialization;

namespace Seiko.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SeiManifest))]
[JsonSerializable(typeof(SourceIndex))]
[JsonSerializable(typeof(PackageIndex))]
[JsonSerializable(typeof(SeikoLock))]
[JsonSerializable(typeof(LocalState))]
public partial class SeikoJsonContext : JsonSerializerContext
{
}
