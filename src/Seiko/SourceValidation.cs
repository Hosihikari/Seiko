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

            if (entry.Artifacts is null)
            {
                throw Fail(
                    $"source '{sourceId}': {name} {entry.Version} has no artifact list",
                    code);
            }

            foreach (var dependency in entry.Dependencies)
            {
                if (dependency is null)
                {
                    throw Fail(
                        $"source '{sourceId}': invalid dependency for {name} {entry.Version}",
                        code);
                }
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
            }
        }
    }

    private static SeikoException Fail(string message, string code) => new(message, code);
}
