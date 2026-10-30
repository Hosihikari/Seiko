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

// Resolution collects every constraint on a package and re-selects when a
// later edge conflicts with an earlier choice, so a satisfiable graph is not
// rejected just because the first constraint was satisfied by a version that
// a subsequent constraint rules out.
public sealed class Resolver
{
    private const int MaxSteps = 10000;
    private const int MaxPrunes = 100;

    private readonly IReadOnlyList<ISeiSource> _sources;
    private readonly SeikoLock? _lock;
    private readonly string _rid;
    private readonly bool _allowPrerelease;
    private readonly IReadOnlySet<string>? _updateNames;
    private readonly Dictionary<string, ResolvedSei> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Constraint>> _constraints = new(StringComparer.Ordinal);
    private readonly Queue<string> _pending = new();
    private int _steps;

    public Resolver(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock? existingLock,
        string rid,
        bool allowPrerelease,
        IReadOnlySet<string>? updateNames = null)
    {
        _sources = sources;
        _lock = existingLock;
        _rid = rid;
        _allowPrerelease = allowPrerelease;
        _updateNames = updateNames;
    }

    public IReadOnlyList<ResolvedSei> Resolve(IReadOnlyList<SeiSpec> specs)
    {
        var directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!SeiName.IsValid(spec.Name))
            {
                throw Fail($"invalid sei name '{spec.Name}'");
            }

            directNames.Add(spec.Name);
            AddConstraint(spec.Name, ParseRange(spec.Version, spec.Name), spec.Version, null);
            _pending.Enqueue(spec.Name);
        }

        Stabilize();

        foreach (var sei in _resolved.Values)
        {
            sei.Direct = directNames.Contains(sei.Name);
        }

        SelectArtifacts();
        return _resolved.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private void Stabilize()
    {
        var prunes = 0;
        while (true)
        {
            while (_pending.Count > 0)
            {
                Select(_pending.Dequeue());
            }

            if (!PruneUnreachable())
            {
                return;
            }

            if (++prunes > MaxPrunes)
            {
                throw Fail("dependency resolution did not converge");
            }
        }
    }

    private void Select(string name)
    {
        if (!_constraints.TryGetValue(name, out var constraints) || constraints.Count == 0)
        {
            return;
        }

        if (!SeiName.IsValid(name))
        {
            throw Fail($"invalid sei name '{name}'");
        }

        if (++_steps > MaxSteps)
        {
            throw Fail("dependency resolution did not converge");
        }

        var source = FindSource(name, out var index);
        var candidates = Candidates(name, source, index!, constraints);
        if (candidates.Count == 0)
        {
            throw Conflict(name, source.Id, constraints);
        }

        var (entry, version) = Choose(name, candidates);
        if (_resolved.TryGetValue(name, out var existing)
            && existing.ParsedVersion.Equals(version))
        {
            return;
        }

        if (existing is not null)
        {
            RemoveOutgoing(name);
        }

        _resolved[name] = new ResolvedSei
        {
            Name = name,
            ParsedVersion = version,
            Entry = entry,
            Source = source,
        };

        foreach (var dependency in entry.Dependencies)
        {
            AddConstraint(
                dependency.Name,
                ParseRange(dependency.Version, dependency.Name),
                dependency.Version,
                name);
            _pending.Enqueue(dependency.Name);
        }
    }

    private List<(SeiVersionEntry Entry, NuGetVersion Version)> Candidates(
        string name,
        ISeiSource source,
        SeiIndex index,
        List<Constraint> constraints)
    {
        var lockedVersion = GetLockedVersion(name);
        var allowPrerelease = _allowPrerelease
            || constraints.Any(constraint => constraint.RangeText?.Contains('-') == true);
        var candidates = new List<(SeiVersionEntry Entry, NuGetVersion Version)>();

        foreach (var entry in index.Versions)
        {
            if (!NuGetVersion.TryParse(entry.Version, out var version))
            {
                throw Fail($"source '{source.Id}': invalid version '{entry.Version}' for {name}");
            }

            if (entry.Yanked && !(lockedVersion is not null && version.Equals(lockedVersion)))
            {
                continue;
            }

            if (!constraints.All(constraint => constraint.Range.Satisfies(version)))
            {
                continue;
            }

            if (version.IsPrerelease && !allowPrerelease)
            {
                continue;
            }

            candidates.Add((entry, version));
        }

        return candidates;
    }

    private (SeiVersionEntry Entry, NuGetVersion Version) Choose(
        string name,
        List<(SeiVersionEntry Entry, NuGetVersion Version)> candidates)
    {
        var chosen = candidates.OrderByDescending(candidate => candidate.Version).First();
        var lockedVersion = GetLockedVersion(name);
        if (lockedVersion is not null)
        {
            var lockedMatch = candidates.FirstOrDefault(candidate => candidate.Version.Equals(lockedVersion));
            if (lockedMatch.Entry is not null)
            {
                chosen = lockedMatch;
            }
        }

        return chosen;
    }

    private void AddConstraint(string name, VersionRange range, string? rangeText, string? from)
    {
        if (!_constraints.TryGetValue(name, out var list))
        {
            list = [];
            _constraints[name] = list;
        }

        list.Add(new Constraint(range, rangeText, from));
    }

    private void RemoveOutgoing(string name)
    {
        foreach (var (target, list) in _constraints)
        {
            if (list.RemoveAll(constraint => string.Equals(constraint.From, name, StringComparison.Ordinal)) > 0)
            {
                _pending.Enqueue(target);
            }
        }
    }

    private bool PruneUnreachable()
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(
            _constraints
                .Where(pair => pair.Value.Any(constraint => constraint.From is null))
                .Select(pair => pair.Key));

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!reachable.Add(name))
            {
                continue;
            }

            if (_resolved.TryGetValue(name, out var resolved))
            {
                foreach (var dependency in resolved.Entry.Dependencies)
                {
                    queue.Enqueue(dependency.Name);
                }
            }
        }

        var removed = false;
        foreach (var name in _resolved.Keys.ToList())
        {
            if (reachable.Contains(name))
            {
                continue;
            }

            RemoveOutgoing(name);
            _resolved.Remove(name);
            _constraints.Remove(name);
            removed = true;
        }

        if (!removed)
        {
            return false;
        }

        foreach (var name in reachable)
        {
            _pending.Enqueue(name);
        }

        return true;
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
        if (_updateNames?.Contains(name) == true)
        {
            return null;
        }

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

    private static SeikoException Conflict(string name, string sourceId, List<Constraint> constraints)
    {
        var parts = constraints
            .Select(constraint =>
                $"{constraint.Range} (required by {(constraint.From is null ? "direct" : constraint.From)})")
            .Distinct(StringComparer.Ordinal);
        return new SeikoException(
            $"version conflict for {name} in source '{sourceId}': no version satisfies {string.Join(", ", parts)}",
            "resolve");
    }

    private static SeikoException Fail(string message) => new(message, "resolve");

    private sealed record Constraint(VersionRange Range, string? RangeText, string? From);
}
