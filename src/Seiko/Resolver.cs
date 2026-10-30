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

// Depth-first search over package versions with chronological backtracking.
// A candidate that makes a later requirement unsatisfiable is abandoned, so
// the resolver can fall back to an older version of an upstream package when
// that version avoids the conflicting constraint entirely.
public sealed class Resolver
{
    private const int MaxAssignments = 100000;

    private readonly IReadOnlyList<ISeiSource> _sources;
    private readonly SeikoLock? _lock;
    private readonly string _rid;
    private readonly bool _allowPrerelease;
    private readonly IReadOnlySet<string>? _updateNames;
    private readonly List<Requirement> _pending = [];
    private readonly Dictionary<string, ResolvedSei> _assigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VersionRange>> _prereleaseHints = new(StringComparer.Ordinal);
    private SeikoException? _lastConflict;
    private int _assignments;

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
        _pending.Clear();
        _assigned.Clear();
        _prereleaseHints.Clear();
        _lastConflict = null;
        _assignments = 0;

        var directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!SeiName.IsValid(spec.Name))
            {
                throw Fail($"invalid sei name '{spec.Name}'");
            }

            directNames.Add(spec.Name);
            _pending.Add(CreateRequirement(spec.Name, spec.Version, null));
        }

        if (!Search(0))
        {
            throw _lastConflict ?? Fail("no version satisfies the declared ranges");
        }

        foreach (var sei in _assigned.Values)
        {
            sei.Direct = directNames.Contains(sei.Name);
        }

        SelectArtifacts();
        return _assigned.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private bool Search(int index)
    {
        if (index >= _pending.Count)
        {
            return true;
        }

        var requirement = _pending[index];
        if (!SeiName.IsValid(requirement.Name))
        {
            throw Fail($"invalid sei name '{requirement.Name}'");
        }

        if (_assigned.TryGetValue(requirement.Name, out var existing))
        {
            if (requirement.Range.Satisfies(existing.ParsedVersion))
            {
                return Search(index + 1);
            }

            _lastConflict = new SeikoException(
                $"version conflict for {requirement.Name}: selected {existing.Version}, but {requirement.Range} is required by {Describe(requirement.From)}",
                "resolve");
            return false;
        }

        var source = FindSource(requirement.Name, out var indexData);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            // Recompute the candidate list on every attempt: a failed branch
            // may have revealed a prerelease constraint for this package, and
            // the hint has to widen the list here too, not only in the branch
            // that discovered it.
            var candidates = Candidates(requirement.Name, source, indexData!, _pending);
            if (candidates.Count == 0)
            {
                _lastConflict = Conflict(requirement.Name, source.Id, _pending);
                return false;
            }

            var next = Order(requirement.Name, candidates)
                .FirstOrDefault(candidate => !tried.Contains(candidate.Version.ToNormalizedString()));
            if (next.Entry is null)
            {
                return false;
            }

            tried.Add(next.Version.ToNormalizedString());
            if (++_assignments > MaxAssignments)
            {
                throw Fail("dependency resolution did not converge");
            }

            _assigned[requirement.Name] = new ResolvedSei
            {
                Name = requirement.Name,
                ParsedVersion = next.Version,
                Entry = next.Entry,
                Source = source,
            };

            var added = 0;
            foreach (var dependency in next.Entry.Dependencies)
            {
                if (!SeiName.IsValid(dependency.Name))
                {
                    throw Fail($"invalid sei name '{dependency.Name}'");
                }

                _pending.Add(CreateRequirement(dependency.Name, dependency.Version, requirement.Name));
                added++;
            }

            if (Search(index + 1))
            {
                return true;
            }

            _pending.RemoveRange(_pending.Count - added, added);
            _assigned.Remove(requirement.Name);
        }
    }

    private List<(SeiVersionEntry Entry, NuGetVersion Version)> Candidates(
        string name,
        ISeiSource source,
        SeiIndex index,
        List<Requirement> pending)
    {
        var constraints = pending
            .Where(requirement => string.Equals(requirement.Name, name, StringComparison.Ordinal))
            .ToList();
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

            if (version.IsPrerelease && !allowPrerelease && !HintAllows(name, version))
            {
                continue;
            }

            candidates.Add((entry, version));
        }

        return candidates;
    }

    private IEnumerable<(SeiVersionEntry Entry, NuGetVersion Version)> Order(
        string name,
        List<(SeiVersionEntry Entry, NuGetVersion Version)> candidates)
    {
        var explicitPrerelease = _allowPrerelease
            || _pending.Any(requirement =>
                string.Equals(requirement.Name, name, StringComparison.Ordinal)
                && requirement.RangeText?.Contains('-') == true);
        var ordered = explicitPrerelease
            ? candidates.OrderByDescending(candidate => candidate.Version).ToList()
            : candidates
                .OrderBy(candidate => candidate.Version.IsPrerelease)
                .ThenByDescending(candidate => candidate.Version)
                .ToList();
        var lockedVersion = GetLockedVersion(name);
        if (lockedVersion is not null)
        {
            var locked = ordered.FirstOrDefault(candidate => candidate.Version.Equals(lockedVersion));
            if (locked.Entry is not null)
            {
                ordered.Remove(locked);
                ordered.Insert(0, locked);
            }
        }

        return ordered;
    }

    private void SelectArtifacts()
    {
        var graphNames = _assigned.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var sei in _assigned.Values)
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

    private Requirement CreateRequirement(string name, string? version, string? from)
    {
        var range = ParseRange(version, name);
        if (version?.Contains('-') == true)
        {
            if (!_prereleaseHints.TryGetValue(name, out var ranges))
            {
                ranges = [];
                _prereleaseHints[name] = ranges;
            }

            ranges.Add(range);
        }

        return new Requirement(name, range, version, from);
    }

    private bool HintAllows(string name, NuGetVersion version)
        => _prereleaseHints.TryGetValue(name, out var ranges)
            && ranges.Any(range => range.Satisfies(version));

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

    private static SeikoException Conflict(string name, string sourceId, List<Requirement> pending)
    {
        var parts = pending
            .Where(requirement => string.Equals(requirement.Name, name, StringComparison.Ordinal))
            .Select(requirement =>
                $"{requirement.Range} (required by {Describe(requirement.From)})")
            .Distinct(StringComparer.Ordinal);
        return new SeikoException(
            $"version conflict for {name} in source '{sourceId}': no version satisfies {string.Join(", ", parts)}",
            "resolve");
    }

    private static string Describe(string? from) => from is null ? "direct" : from;

    private static SeikoException Fail(string message) => new(message, "resolve");

    private sealed record Requirement(
        string Name,
        VersionRange Range,
        string? RangeText,
        string? From);
}
