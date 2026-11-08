using NuGet.Versioning;

namespace Seiko;

// Two artifacts have the same resolution conditions when the resolver cannot
// tell them apart: the same rid and the same normalized prerequisite ranges.
// The index verifier and the cross-source check must agree with the resolver
// on this, so both call this helper (ADR-0027, ADR-0036).
internal static class ArtifactConditions
{
    public static bool Equivalent(ArtifactEntry left, ArtifactEntry right)
        => string.Equals(
            Key(left.Rid, left.Prerequisites),
            Key(right.Rid, right.Prerequisites),
            StringComparison.Ordinal);

    // Origin selectors carry the same shape as artifact conditions, so the
    // ambiguity test there uses this too (ADR-0044).
    public static string Key(string? rid, List<SeiDependency>? prerequisites)
        => NormalizeRid(rid) + "\n" + NormalizePrerequisites(prerequisites);

    private static string NormalizeRid(string? rid)
        => string.IsNullOrWhiteSpace(rid) ? "*" : rid.Trim();

    private static string NormalizePrerequisites(List<SeiDependency>? prerequisites)
        => string.Join(
            "\n",
            (prerequisites ?? [])
                .Where(relation => relation is not null)
                .Select(relation => $"{relation.Name}\n{NormalizeRange(relation.Version)}")
                .OrderBy(text => text, StringComparer.Ordinal));

    private static string NormalizeRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
        {
            return "*";
        }

        return VersionRange.TryParse(range, out var parsed)
            ? parsed.ToNormalizedString()
            : range;
    }
}
