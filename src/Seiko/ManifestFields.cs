using System.Text.Json;

namespace Seiko;

// The version gate has to see whether a field was *written*, not whether the
// deserialized object holds a value: an explicit `"preserve": null` reads back
// as null exactly like an omitted field, but a 3 manifest wrote it, so it is
// still a declaration. The raw document answers the question the object
// cannot.
internal static class ManifestFields
{
    public static (bool Preserve, bool DependencyGroups) Gated(byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        if (
            !document.RootElement.TryGetProperty("artifact", out var artifact)
            || artifact.ValueKind != JsonValueKind.Object
        )
        {
            return (false, false);
        }

        return (
            artifact.TryGetProperty("preserve", out _),
            artifact.TryGetProperty("dependencyGroups", out _)
        );
    }

    public static string? GatedText(bool preserve, bool groups) =>
        (preserve, groups) switch
        {
            (true, true) => "preserve or dependencyGroups",
            (true, false) => "preserve",
            (false, true) => "dependencyGroups",
            _ => null,
        };
}
