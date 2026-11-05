using NuGet.Versioning;

namespace Seiko;

// First-source-wins selects the graph. For every selected version, other
// sources must still agree on artifacts that have identical conditions; a
// mirror may not serve different bytes for the same public artifact.
internal static class CrossSourceCheck
{
    public static void Verify(
        IReadOnlyList<ISeiSource> sources,
        IEnumerable<ResolvedSei> resolved,
        string code)
    {
        // Each name is a separate SeiIndex within the same source, so the
        // cache key covers source id and name.
        var validated = new HashSet<(string Source, string Name)>();
        foreach (var sei in resolved)
        {
            foreach (var source in sources)
            {
                if (ReferenceEquals(source, sei.Source)
                    || string.Equals(source.Id, sei.Source.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                var index = source.GetSei(sei.Name);
                if (index is null)
                {
                    continue;
                }

                if (validated.Add((source.Id, sei.Name)))
                {
                    SourceValidation.EnsureIndex(index, source.Id, code);
                }

                var matches = index.Versions
                    .Where(entry => NuGetVersion.TryParse(entry.Version, out var version)
                        && version.Equals(sei.ParsedVersion))
                    .ToList();
                if (matches.Count == 0)
                {
                    continue;
                }

                if (matches.Count > 1)
                {
                    throw new SeikoException(
                        $"source '{source.Id}': duplicate version '{sei.Version}' for {sei.Name}",
                        code);
                }

                foreach (var artifact in matches[0].Artifacts ?? [])
                {
                    if (artifact?.Source is null)
                    {
                        throw new SeikoException(
                            $"source '{source.Id}': invalid artifact for {sei.Name} {sei.Version}",
                            code);
                    }

                    if (!ArtifactConditions.Equivalent(sei.Artifact, artifact))
                    {
                        continue;
                    }

                    if (!string.Equals(
                        artifact.Source.Sha256,
                        sei.Artifact.Source.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SeikoException(
                            $"artifact digest for {sei.Name} {sei.Version} differs between "
                            + $"source '{sei.Source.Id}' ({sei.Artifact.Source.Sha256}) and "
                            + $"source '{source.Id}' ({artifact.Source.Sha256})",
                            "checksum");
                    }
                }
            }
        }
    }

}
