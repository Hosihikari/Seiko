namespace Seiko;

// Artifact-level dependency groups (ADR-0045): mutually exclusive candidate
// relation sets. A resolution candidate is the pair (artifact, group); the
// selected group's relations are added to the artifact's common ones.
internal static class DependencyGroups
{
    public static DependencyGroup? At(ArtifactEntry artifact, int? index)
        => index is { } value
            && artifact.DependencyGroups is { } groups
            && value >= 0
            && value < groups.Count
                ? groups[value]
                : null;

    // The artifact's common relations come first so diagnostics keep the
    // scope order used before groups existed.
    public static List<SeiDependency> Merge(
        List<SeiDependency>? common,
        List<SeiDependency>? scoped)
        => [.. common ?? [], .. scoped ?? []];

    public static List<SeiDependency> Dependencies(ArtifactEntry artifact, int? index)
        => Merge(artifact.Dependencies, At(artifact, index)?.Dependencies);

    public static List<SeiDependency> Prerequisites(ArtifactEntry artifact, int? index)
        => Merge(artifact.Prerequisites, At(artifact, index)?.Prerequisites);

    public static List<SeiDependency> OptionalPeers(ArtifactEntry artifact, int? index)
        => Merge(artifact.OptionalPeers, At(artifact, index)?.OptionalPeers);

    public static List<SeiDependency> Conflicts(ArtifactEntry artifact, int? index)
        => Merge(artifact.Conflicts, At(artifact, index)?.Conflicts);

    // Specificity is counted over distinct prerequisite sei names. The
    // artifact level scores one point per name; the selected group adds at
    // most one point, and only when it introduces a name the artifact level
    // did not already score. Version-level relations and the other three
    // kinds never score (ADR-0045).
    public static int Score(ArtifactEntry artifact, int? index)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prerequisite in artifact.Prerequisites ?? [])
        {
            if (prerequisite is not null)
            {
                names.Add(prerequisite.Name);
            }
        }

        var score = names.Count;
        if (At(artifact, index)?.Prerequisites is { } scoped
            && scoped.Any(prerequisite => prerequisite is not null && !names.Contains(prerequisite.Name)))
        {
            score++;
        }

        return score;
    }

    // Group order is meaningful: the lock records the selected position, so a
    // reordered list is a metadata change even when the sets are equal.
    public static bool Equal(
        IReadOnlyList<DependencyGroup>? left,
        IReadOnlyList<DependencyGroup>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        return a.Count == b.Count
            && a.Zip(b).All(pair => pair.First is not null
                && pair.Second is not null
                && RelationsEqual(pair.First.Dependencies, pair.Second.Dependencies)
                && RelationsEqual(pair.First.Prerequisites, pair.Second.Prerequisites)
                && RelationsEqual(pair.First.OptionalPeers, pair.Second.OptionalPeers)
                && RelationsEqual(pair.First.Conflicts, pair.Second.Conflicts));
    }

    // Applicability conditions are the two gates: a positive one
    // (prerequisites) and a negative one (conflicts). Dependencies and
    // optional peers are part of what the group installs or constrains, not
    // part of when it applies. Repeated entries are collapsed: [a] and [a, a]
    // gate exactly alike.
    public static string ConditionKey(DependencyGroup group)
        => ArtifactConditions.Key(null, Distinct(group.Prerequisites))
            + "\n--\n"
            + ArtifactConditions.Key(null, Distinct(group.Conflicts));

    private static List<SeiDependency> Distinct(IEnumerable<SeiDependency>? relations)
        => (relations ?? [])
            .Where(relation => relation is not null)
            .DistinctBy(
                relation => $"{relation.Name}|{relation.Version ?? ""}",
                StringComparer.Ordinal)
            .ToList();

    // The rule the index verifier and the origin reader share: two groups
    // gated identically but installing different relations cannot be told
    // apart (ADR-0045).
    public static int? FindAmbiguousPair(IReadOnlyList<DependencyGroup?> groups)
    {
        for (var right = 1; right < groups.Count; right++)
        {
            if (groups[right] is not { } current)
            {
                continue;
            }

            for (var left = 0; left < right; left++)
            {
                if (groups[left] is { } existing
                    && string.Equals(
                        ConditionKey(existing),
                        ConditionKey(current),
                        StringComparison.Ordinal)
                    && !string.Equals(
                        ResultKey(existing),
                        ResultKey(current),
                        StringComparison.Ordinal))
                {
                    return right;
                }
            }
        }

        return null;
    }

    // The install side of a group. Two groups that apply under the same
    // conditions but differ here would let environment and source drift pick
    // one silently, so index verification rejects that shape (ADR-0045).
    public static string ResultKey(DependencyGroup group)
        => ArtifactConditions.Key(null, group.Dependencies)
            + "\n--\n"
            + ArtifactConditions.Key(null, group.OptionalPeers);

    public static bool RelationsEqual(
        IReadOnlyList<SeiDependency>? left,
        IReadOnlyList<SeiDependency>? right)
    {
        var a = (left ?? [])
            .Where(relation => relation is not null)
            .OrderBy(relation => relation.Name, StringComparer.Ordinal)
            .ThenBy(relation => relation.Version ?? "", StringComparer.Ordinal)
            .ToList();
        var b = (right ?? [])
            .Where(relation => relation is not null)
            .OrderBy(relation => relation.Name, StringComparer.Ordinal)
            .ThenBy(relation => relation.Version ?? "", StringComparer.Ordinal)
            .ToList();
        return a.Count == (left ?? []).Count
            && b.Count == (right ?? []).Count
            && a.Count == b.Count
            && a.Zip(b).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && string.Equals(pair.First.Version ?? "", pair.Second.Version ?? "", StringComparison.Ordinal));
    }
}
