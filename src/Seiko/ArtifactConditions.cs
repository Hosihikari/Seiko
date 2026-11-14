using NuGet.Versioning;

namespace Seiko;

// Two artifacts have the same resolution conditions when the resolver cannot
// tell them apart: the same rid, the same normalized prerequisite ranges and
// the same group conditions. The index verifier and the cross-source check
// must agree with the resolver on this, so both call this helper (ADR-0027,
// ADR-0036, ADR-0045).
internal static class ArtifactConditions
{
    public static bool Equivalent(ArtifactEntry left, ArtifactEntry right)
        => string.Equals(Key(left), Key(right), StringComparison.Ordinal);

    private static string Key(ArtifactEntry artifact)
        => Key(artifact.Rid, artifact.Prerequisites) + "\n#\n" + GroupConditions(artifact);

    // A group set can carry conditions the artifact-level part does not; two
    // artifacts whose groups sit under different gates are distinguishable
    // even when the artifact-level keys match. The lock records the group
    // position, but applicability does not depend on it, so the conditions
    // are sorted and exact duplicates collapse: reordering or repeating a
    // group must not make two artifacts that apply in exactly the same
    // environments compare unequal (the cross-source digest check and the
    // same-index equivalence check both rely on this). ADR-0045 lets a
    // repeated group stand, so `[G]` and `[G, G]` are the same environment.
    internal static string GroupConditions(ArtifactEntry artifact)
        => string.Join(
            "\n#\n",
            (artifact.DependencyGroups ?? [])
                .Select(group => group is null
                    ? "?"
                    : DependencyGroups.ConditionKey(group))
                .OrderBy(text => text, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal));

    // Origin selectors carry the same shape as artifact conditions, so the
    // ambiguity test there uses this too (ADR-0044).
    public static string Key(string? rid, List<SeiDependency>? prerequisites)
        => NormalizeRid(rid) + "\n" + NormalizePrerequisites(prerequisites);

    private static string NormalizeRid(string? rid)
        => string.IsNullOrWhiteSpace(rid) ? "*" : rid.Trim();

    // Normalized text is deduplicated, not the raw entries: `[a]` and
    // `[a, a@*]` both normalize to the same line and must gate alike. Two
    // different ranges of one name stay separate lines, so the result is a
    // set of normalized conditions.
    private static string NormalizePrerequisites(List<SeiDependency>? prerequisites)
        => string.Join(
            "\n",
            (prerequisites ?? [])
                .Where(relation => relation is not null)
                .Select(relation => $"{relation.Name}\n{NormalizeRange(relation.Version)}")
                .Distinct(StringComparer.Ordinal)
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
