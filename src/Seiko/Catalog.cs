using System.Text.Json;

namespace Seiko;

public sealed class CatalogEntry
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public string? License { get; set; }
    public bool Unlisted { get; set; }
    public string? Deprecated { get; set; }
    public AdvisoryInfo? Advisory { get; set; }
}

// The source-root catalog lets search, filters and `outdated` run from one
// fetch instead of one request per matching name (ADR-0035). It is derived
// data: a missing or broken catalog silently falls back to per-name reads,
// and it is never used to decide what to install.
public sealed class SeiCatalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CatalogEntry> Sei { get; set; } = [];
}

internal static class CatalogFile
{
    public const string FileName = "catalog.json";

    public static SeiCatalog Build(IEnumerable<SeiIndex> sei)
    {
        var entries = new List<CatalogEntry>();
        foreach (var item in sei)
        {
            var latest = item.Versions
                .Where(entry => !entry.Yanked)
                .OrderByDescending(entry => Parse(entry.Version))
                .FirstOrDefault();
            var advisory = item.Versions
                .Select(entry => entry.Advisory)
                .Where(entry => entry is not null)
                .OrderByDescending(Rank)
                .FirstOrDefault();
            entries.Add(new CatalogEntry
            {
                Name = item.Name,
                Version = latest?.Version,
                Description = item.Project?.Description,
                Tags = item.Project?.Tags is { } tags
                    ? tags.OrderBy(tag => tag, StringComparer.Ordinal).ToList()
                    : null,
                License = item.Project?.License,
                Unlisted = item.Unlisted,
                Deprecated = item.Deprecated,
                Advisory = advisory,
            });
        }

        return new SeiCatalog
        {
            Sei = entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
        };
    }

    public static SeiCatalog? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var catalog = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                SeikoJsonContext.Default.SeiCatalog);
            if (catalog is not { SchemaVersion: SeiCatalog.CurrentSchemaVersion } || catalog.Sei is null)
            {
                return null;
            }

            return catalog;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An invalid catalog is ignored rather than reported: it is a derived
    // cache, and the caller still has the per-name path.
    public static void Write(string path, SeiCatalog catalog)
        => File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(catalog, SeikoJsonContext.Default.SeiCatalog));

    private static NuGet.Versioning.NuGetVersion Parse(string version)
        => NuGet.Versioning.NuGetVersion.TryParse(version, out var parsed)
            ? parsed
            : new NuGet.Versioning.NuGetVersion(0, 0, 0);

    private static int Rank(AdvisoryInfo? advisory)
        => advisory?.Severity?.Trim().ToLowerInvariant() switch
        {
            "critical" => 3,
            "high" => 2,
            "moderate" => 1,
            "low" => 0,
            _ => -1,
        };
}
