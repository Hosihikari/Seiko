using NuGet.Versioning;

namespace Seiko;

public sealed class ResolvedSei
{
    public required string Name { get; init; }
    public required NuGetVersion ParsedVersion { get; init; }
    public required SeiVersionEntry Entry { get; init; }
    public required ISeiSource Source { get; init; }
    public bool Direct { get; set; }
    public ArtifactEntry Artifact { get; set; } = null!;
    public string Version => ParsedVersion.ToNormalizedString();
}

public sealed class Resolver
{
    private readonly IReadOnlyList<ISeiSource> _sources;
    private readonly SeikoLock? _lock;
    private readonly string _rid;
    private readonly bool _allowPrerelease;
    private readonly Dictionary<string, ResolvedSei> _resolved = new(StringComparer.Ordinal);

    public Resolver(IReadOnlyList<ISeiSource> sources, SeikoLock? existingLock, string rid, bool allowPrerelease)
    {
        _sources = sources;
        _lock = existingLock;
        _rid = rid;
        _allowPrerelease = allowPrerelease;
    }

    public IReadOnlyList<ResolvedSei> Resolve(IReadOnlyList<SeiSpec> specs)
    {
        var directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            directNames.Add(spec.Name);
            ResolveSei(spec, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var sei in _resolved.Values)
        {
            sei.Direct = directNames.Contains(sei.Name);
        }

        SelectArtifacts();
        return _resolved.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private void ResolveSei(SeiSpec spec, HashSet<string> path)
    {
        if (path.Contains(spec.Name))
        {
            throw Fail($"dependency cycle: {string.Join(" -> ", path)} -> {spec.Name}");
        }

        var range = ParseRange(spec.Version, spec.Name);
        if (_resolved.TryGetValue(spec.Name, out var existing))
        {
            if (range.Satisfies(existing.ParsedVersion))
            {
                return;
            }

            throw Fail(
                $"version conflict for {spec.Name}: resolved {existing.Version}, but {range} is required");
        }

        var source = FindSource(spec.Name, out var index);
        var lockedVersion = GetLockedVersion(spec.Name);
        var candidates = new List<(SeiVersionEntry Entry, NuGetVersion Version)>();

        foreach (var entry in index!.Versions)
        {
            if (!NuGetVersion.TryParse(entry.Version, out var version))
            {
                throw Fail($"source '{source.Id}': invalid version '{entry.Version}' for {spec.Name}");
            }

            if (entry.Yanked && !(lockedVersion is not null && version.Equals(lockedVersion)))
            {
                continue;
            }

            if (!range.Satisfies(version))
            {
                continue;
            }

            if (version.IsPrerelease && !_allowPrerelease && !(spec.Version?.Contains('-') ?? false))
            {
                continue;
            }

            candidates.Add((entry, version));
        }

        if (candidates.Count == 0)
        {
            throw Fail($"no version of {spec.Name} satisfies {range}");
        }

        var chosen = candidates.OrderByDescending(c => c.Version).First();
        if (lockedVersion is not null)
        {
            var lockedMatch = candidates.FirstOrDefault(c => c.Version.Equals(lockedVersion));
            if (lockedMatch.Entry is not null)
            {
                chosen = lockedMatch;
            }
        }

        var resolved = new ResolvedSei
        {
            Name = spec.Name,
            ParsedVersion = chosen.Version,
            Entry = chosen.Entry,
            Source = source,
        };

        _resolved[spec.Name] = resolved;
        path.Add(spec.Name);
        try
        {
            foreach (var dependency in chosen.Entry.Dependencies)
            {
                ResolveSei(new SeiSpec { Name = dependency.Name, Version = dependency.Version }, path);
            }
        }
        finally
        {
            path.Remove(spec.Name);
        }
    }

    private void SelectArtifacts()
    {
        var graphNames = _resolved.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var sei in _resolved.Values)
        {
            var scored = new List<(ArtifactEntry Artifact, int Score)>();
            foreach (var artifact in sei.Entry.Artifacts)
            {
                var score = Score(artifact, graphNames);
                if (score >= 0)
                {
                    scored.Add((artifact, score));
                }
            }

            if (scored.Count == 0)
            {
                throw Fail(
                    $"no artifact of {sei.Name} {sei.Version} matches rid '{_rid}' and sei [{string.Join(", ", graphNames.OrderBy(n => n, StringComparer.Ordinal))}]");
            }

            var best = scored.Max(s => s.Score);
            var top = scored.Where(s => s.Score == best).ToList();
            if (top.Count > 1)
            {
                throw Fail(
                    $"ambiguous artifact for {sei.Name} {sei.Version}: {top.Count} artifacts match at specificity {best}");
            }

            sei.Artifact = top[0].Artifact;
        }
    }

    private int Score(ArtifactEntry artifact, HashSet<string> graphNames)
    {
        if (artifact.Selectors is null || artifact.Selectors.Count == 0)
        {
            return 0;
        }

        foreach (var (key, value) in artifact.Selectors)
        {
            if (key.Equals("rid", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals(_rid, StringComparison.OrdinalIgnoreCase))
                {
                    return -1;
                }
            }
            else if (!graphNames.Contains(value))
            {
                return -1;
            }
        }

        return artifact.Selectors.Count;
    }

    private ISeiSource FindSource(string name, out SeiIndex? index)
    {
        foreach (var source in _sources)
        {
            index = source.GetSei(name);
            if (index is not null)
            {
                return source;
            }
        }

        throw Fail($"sei '{name}' not found in any source");
    }

    private NuGetVersion? GetLockedVersion(string name)
    {
        var locked = _lock?.Sei.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (locked is null)
        {
            return null;
        }

        return NuGetVersion.TryParse(locked.Version, out var version) ? version : null;
    }

    private static VersionRange ParseRange(string? text, string sei)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*")
        {
            return VersionRange.All;
        }

        if (!VersionRange.TryParse(text, out var range))
        {
            throw Fail($"invalid version range '{text}' for {sei}");
        }

        return range;
    }

    private static SeikoException Fail(string message) => new(message, "resolve");
}
