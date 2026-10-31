namespace Seiko;

internal static class Retirement
{
    public static bool IsCritical(SeiVersionEntry entry)
        => string.Equals(entry.Advisory?.Severity?.Trim(), "critical", StringComparison.OrdinalIgnoreCase);

    public static string? EffectiveDeprecated(SeiVersionEntry entry, string? packageDeprecated)
        => string.IsNullOrWhiteSpace(entry.Deprecated) ? packageDeprecated : entry.Deprecated;

    public static IReadOnlyList<string> Warnings(IReadOnlyList<ResolvedSei> resolved)
    {
        var warnings = new List<string>();
        foreach (var sei in resolved.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var parts = new List<string>();
            if (sei.Entry.Yanked)
            {
                parts.Add("yanked");
            }

            if (IsCritical(sei.Entry))
            {
                var url = sei.Entry.Advisory?.Url;
                parts.Add(string.IsNullOrWhiteSpace(url) ? "critical" : $"critical ({url})");
            }

            var deprecated = EffectiveDeprecated(sei.Entry, sei.PackageDeprecated);
            if (!string.IsNullOrWhiteSpace(deprecated))
            {
                parts.Add($"deprecated: {deprecated}");
            }

            if (parts.Count > 0)
            {
                warnings.Add($"{sei.Name} {sei.Version} is {string.Join(", ", parts)}");
            }
        }

        return warnings;
    }
}
