namespace Seiko;

// The index carries an overlay set by the source maintainers next to the
// maintainer's own fields (ADR-0033). Clients merge it into the standard
// retirement fields when reading, so every downstream consumer — including
// old 0.3 clients that only know the standard fields — sees the effective
// state without a second code path.
internal static class IndexOverlay
{
    private static readonly string[] SeverityOrder = ["low", "moderate", "high", "critical"];

    public static SeiIndex Apply(SeiIndex sei)
    {
        sei.Unlisted = sei.Unlisted || sei.SourceUnlisted == true;
        if (!string.IsNullOrWhiteSpace(sei.SourceDeprecated))
        {
            sei.Deprecated = sei.SourceDeprecated;
        }

        // A null version list stays null so downstream validation still
        // reports the source document as malformed.
        foreach (var entry in sei.Versions ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            entry.Yanked = entry.Yanked || entry.SourceYanked == true;
            entry.Advisory = MostSevere(entry.Advisory, entry.SourceAdvisories);
        }

        return sei;
    }

    public static bool IsSourceOrigin(SeiVersionEntry entry)
        => entry.SourceYanked == true
            || (entry.SourceAdvisories is { Count: > 0 }
                && MostSevere(entry.Advisory, entry.SourceAdvisories) is { } chosen
                && entry.SourceAdvisories.Contains(chosen));

    private static AdvisoryInfo? MostSevere(
        AdvisoryInfo? maintainer,
        IReadOnlyList<AdvisoryInfo>? source)
    {
        var best = maintainer;
        foreach (var candidate in source ?? [])
        {
            if (Rank(candidate) > Rank(best))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int Rank(AdvisoryInfo? advisory)
    {
        var severity = advisory?.Severity?.Trim();
        var index = severity is null
            ? -1
            : Array.FindIndex(SeverityOrder, item =>
                string.Equals(item, severity, StringComparison.OrdinalIgnoreCase));
        return index;
    }
}
