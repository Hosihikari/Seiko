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

        if (value.SchemaVersion != 1)
        {
            throw new SeikoException($"unsupported lock schemaVersion {value.SchemaVersion}", "lock");
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
                        Selectors = SortSelectors(p.Artifact.Selectors),
                    },
                    Dependencies = p.Entry.Dependencies
                        .OrderBy(d => d.Name, StringComparer.Ordinal)
                        .Select(d => new SeiDependency { Name = d.Name, Version = d.Version })
                        .ToList(),
                })
                .ToList(),
        };
    }

    private static Dictionary<string, string>? SortSelectors(Dictionary<string, string>? selectors)
    {
        if (selectors is null || selectors.Count == 0)
        {
            return null;
        }

        return selectors
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
