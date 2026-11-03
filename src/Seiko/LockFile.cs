using System.Text;
using System.Text.Json;

namespace Seiko;

public static class LockFile
{
    public static SeikoLock? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var value = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.SeikoLock)
            ?? throw new SeikoException($"invalid lock file: {path}", "lock");

        // Older locks are not migrated in v0; ADR-0031 maps that to `io`,
        // while a structurally broken current lock stays `lock`.
        if (value.SchemaVersion != SeikoLock.CurrentSchemaVersion)
        {
            throw new SeikoException($"unsupported lock schemaVersion {value.SchemaVersion}", "io");
        }

        if (value.Target is null || value.Sources is null || value.Sei is null)
        {
            throw new SeikoException($"invalid lock file: {path}", "lock");
        }

        if (value.Sources.Any(source => source is null))
        {
            throw new SeikoException($"invalid lock file: {path}", "lock");
        }

        foreach (var entry in value.Sei)
        {
            if (entry is null
                || entry.Artifact is null
                || entry.Dependencies is null
                || entry.Prerequisites is null
                || entry.OptionalPeers is null
                || entry.Conflicts is null
                || entry.Artifact.Dependencies is null
                || entry.Artifact.Prerequisites is null
                || entry.Artifact.OptionalPeers is null
                || entry.Artifact.Conflicts is null)
            {
                throw new SeikoException($"invalid lock file: {path}", "lock");
            }

            // Relation elements are read directly by replay; a JSON `null`
            // would otherwise surface as an internal NRE.
            if (entry.Dependencies.Concat(entry.Prerequisites)
                    .Concat(entry.OptionalPeers)
                    .Concat(entry.Conflicts)
                    .Any(relation => relation is null)
                || entry.Artifact.Dependencies.Concat(entry.Artifact.Prerequisites)
                    .Concat(entry.Artifact.OptionalPeers)
                    .Concat(entry.Artifact.Conflicts)
                    .Any(relation => relation is null))
            {
                throw new SeikoException($"invalid lock file: {path}", "lock");
            }
        }

        return value;
    }

    public static void Write(string path, SeikoLock value)
    {
        var json = JsonSerializer.Serialize(value, SeikoJsonContext.Default.SeikoLock)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    public static SeikoLock FromResolution(
        string rid,
        IReadOnlyList<ISeiSource> sources,
        IReadOnlyList<ResolvedSei> sei)
    {
        return new SeikoLock
        {
            Target = new PlatformInfo { Rid = rid },
            Sources = sources.Select(s => s.Id).ToList(),
            Sei = sei
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new LockedSei
                {
                    Name = p.Name,
                    Version = p.Version,
                    Source = p.Source.Id,
                    Direct = p.Direct,
                    Artifact = new LockedArtifact
                    {
                        Sha256 = (p.Artifact.Source.Sha256 ?? "").ToLowerInvariant(),
                        Rid = p.Artifact.Rid,
                        Dependencies = SortRelations(p.Artifact.Dependencies),
                        Prerequisites = SortRelations(p.Artifact.Prerequisites),
                        OptionalPeers = SortRelations(p.Artifact.OptionalPeers),
                        Conflicts = SortRelations(p.Artifact.Conflicts),
                    },
                    Dependencies = SortRelations(p.Entry.Dependencies),
                    Prerequisites = SortRelations(p.Entry.Prerequisites),
                    OptionalPeers = SortRelations(p.Entry.OptionalPeers),
                    Conflicts = SortRelations(p.Entry.Conflicts),
                })
                .ToList(),
        };
    }

    private static List<SeiDependency> SortRelations(IEnumerable<SeiDependency>? relations)
        => (relations ?? [])
            .OrderBy(relation => relation.Name, StringComparer.Ordinal)
            .ThenBy(relation => relation.Version ?? "", StringComparer.Ordinal)
            .Select(relation => new SeiDependency { Name = relation.Name, Version = relation.Version })
            .ToList();

}
