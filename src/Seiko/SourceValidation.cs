namespace Seiko;

// Source JSON is untrusted input. System.Text.Json keeps the default
// initializers for missing properties but assigns explicit nulls, so both the
// normal resolver and frozen replay must reject the same broken shapes instead
// of walking a null list.
internal static class SourceValidation
{
    public static void EnsureIndex(SeiIndex index, string sourceId, string code)
    {
        var name = string.IsNullOrEmpty(index.Name) ? "(unnamed)" : index.Name;
        var versions = index.Versions;
        if (versions is null || versions.Count == 0)
        {
            throw Fail($"source '{sourceId}': sei '{name}' has no versions", code);
        }

        foreach (var entry in versions)
        {
            if (entry is null)
            {
                throw Fail($"source '{sourceId}': invalid version entry for {name}", code);
            }

            if (entry.Dependencies is null)
            {
                throw Fail(
                    $"source '{sourceId}': {name} {entry.Version} has no dependency list",
                    code);
            }

            if (entry.Prerequisites is null
                || entry.OptionalPeers is null
                || entry.Conflicts is null)
            {
                throw Fail(
                    $"source '{sourceId}': {name} {entry.Version} has no relation list",
                    code);
            }

            if (entry.Artifacts is null)
            {
                throw Fail(
                    $"source '{sourceId}': {name} {entry.Version} has no artifact list",
                    code);
            }

            var entryRelations = entry.Dependencies
                .Concat(entry.Prerequisites)
                .Concat(entry.OptionalPeers)
                .Concat(entry.Conflicts)
                .ToList();
            foreach (var relation in entryRelations)
            {
                if (relation is null)
                {
                    throw Fail(
                        $"source '{sourceId}': invalid relation for {name} {entry.Version}",
                        code);
                }
            }

            foreach (var relation in entryRelations)
            {
                EnsureNotSelf(name, relation, sourceId, entry.Version, code);
            }

            foreach (var artifact in entry.Artifacts)
            {
                if (artifact is null)
                {
                    throw Fail(
                        $"source '{sourceId}': invalid artifact for {name} {entry.Version}",
                        code);
                }

                if (artifact.Source is null)
                {
                    throw Fail(
                        $"source '{sourceId}': artifact for {name} {entry.Version} has no source",
                        code);
                }

                if (artifact.Dependencies is null
                    || artifact.Prerequisites is null
                    || artifact.OptionalPeers is null
                    || artifact.Conflicts is null)
                {
                    throw Fail(
                        $"source '{sourceId}': artifact for {name} {entry.Version} has no relation list",
                        code);
                }

                var artifactRelations = artifact.Dependencies
                    .Concat(artifact.Prerequisites)
                    .Concat(artifact.OptionalPeers)
                    .Concat(artifact.Conflicts)
                    .ToList();
                foreach (var relation in artifactRelations)
                {
                    if (relation is null)
                    {
                        throw Fail(
                            $"source '{sourceId}': artifact for {name} {entry.Version} has an invalid relation",
                            code);
                    }
                }

                foreach (var relation in artifactRelations)
                {
                    EnsureNotSelf(name, relation, sourceId, entry.Version, code);
                }

                if (artifact.Rid is not null && string.IsNullOrWhiteSpace(artifact.Rid))
                {
                    throw Fail(
                        $"source '{sourceId}': artifact for {name} {entry.Version} has an empty rid",
                        code);
                }

                if (artifact.Layout is not null && artifact.Layout != "root")
                {
                    throw Fail(
                        $"source '{sourceId}': artifact for {name} {entry.Version} has unsupported layout '{artifact.Layout}'",
                        code);
                }
            }
        }
    }

    // A self relation has no meaning in any of the four kinds, and frozen
    // replay rejects it too: accepting it here would produce a lock that
    // cannot replay.
    private static void EnsureNotSelf(
        string name,
        SeiDependency relation,
        string sourceId,
        string? version,
        string code)
    {
        if (string.Equals(relation.Name, name, StringComparison.Ordinal))
        {
            throw Fail(
                $"source '{sourceId}': {name} {version} has a self relation",
                code);
        }
    }

    private static SeikoException Fail(string message, string code) => new(message, code);
}
