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

public static class CatalogFile
{
    public const string FileName = "catalog.json";

    public static SeiCatalog Build(IEnumerable<SeiIndex> sei)
    {
        var entries = new List<CatalogEntry>();
        foreach (var item in sei)
        {
            if (item is null)
            {
                continue;
            }

            // Build from the effective state: the catalog is what search sees,
            // so a source-side yank or advisory must already be merged in. A
            // malformed mirror has no usable version list; the verifier
            // reports that separately, and generation must not crash on it.
            var effective = IndexOverlay.Apply(item);
            var versions = effective.Versions ?? [];
            // Search uses the same helper for its per-name fallback, so the
            // derived catalog cannot change the displayed version.
            var latest = SeikoClient.LatestVersion(effective);
            var advisory = MostSevere(versions);
            entries.Add(new CatalogEntry
            {
                Name = effective.Name ?? "",
                Version = latest?.Version,
                Description = effective.Project?.Description,
                Tags = effective.Project?.Tags is { } tags
                    ? tags.OrderBy(tag => tag, StringComparer.Ordinal).ToList()
                    : null,
                License = effective.Project?.License,
                Unlisted = effective.Unlisted,
                Deprecated = effective.Deprecated,
                Advisory = advisory,
            });
        }

        return new SeiCatalog
        {
            Sei = entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
        };
    }

    // A catalog is derived data; anything structurally wrong makes the whole
    // file unusable and the caller falls back to per-name reads (ADR-0035).
    // Duplicate names would otherwise crash search.
    public static bool IsValid(SeiCatalog? catalog)
    {
        if (catalog is not { SchemaVersion: SeiCatalog.CurrentSchemaVersion } || catalog.Sei is null)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Sei)
        {
            if (entry is null || !SeiName.IsValid(entry.Name) || !names.Add(entry.Name))
            {
                return false;
            }
        }

        return true;
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
            return IsValid(catalog) ? catalog : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // An invalid catalog is ignored rather than reported: it is a derived
    // cache, and the caller still has the per-name path.
    // Atomic same-volume write (ADR-0043): a temp file plus rename means a
    // failure leaves no half catalog, and the rename replaces the directory
    // entry instead of following an existing leaf symlink, so a linked
    // catalog.json cannot redirect the write outside the index directory.
    public static void Write(string path, SeiCatalog catalog)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(directory, "." + FileName + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(
                temp,
                JsonSerializer.SerializeToUtf8Bytes(catalog, SeikoJsonContext.Default.SeiCatalog));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // Reads every sei mirror under an index directory; malformed files are
    // skipped here because the verifier reports them separately.
    public static IEnumerable<SeiIndex> Load(string directory)
    {
        var seiDirectory = Path.Combine(directory, "sei");
        if (!Directory.Exists(seiDirectory))
        {
            yield break;
        }

        foreach (var file in Directory
            .EnumerateFiles(seiDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            SeiIndex? index = null;
            try
            {
                index = JsonSerializer.Deserialize(
                    File.ReadAllBytes(file),
                    SeikoJsonContext.Default.SeiIndex);
            }
            catch (JsonException)
            {
            }

            if (index is not null)
            {
                yield return index;
            }
        }
    }

    // The package's most severe advisory across all versions; search uses the
    // same helper for its per-name fallback, so a catalog's presence cannot
    // change the displayed security signal.
    internal static AdvisoryInfo? MostSevere(IEnumerable<SeiVersionEntry?> versions)
        => versions
            .Select(entry => entry?.Advisory)
            .Where(advisory => advisory is not null)
            .OrderByDescending(Rank)
            .FirstOrDefault();

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
