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
    public string? PackageDeprecated { get; init; }
    public string Version => ParsedVersion.ToNormalizedString();
}

// Depth-first search over package versions with chronological backtracking.
// A candidate that makes a later requirement unsatisfiable is abandoned, so
// the resolver can fall back to an older version of an upstream package when
// that version avoids the conflicting constraint entirely.
public sealed class Resolver
{
    private const long MaxTotalAssignments = 1_000_000;
    private const int MaxSearchDepth = 512;

    private readonly IReadOnlyList<ISeiSource> _sources;
    private readonly SeikoLock? _lock;
    private readonly string _rid;
    private readonly bool _allowPrerelease;
    private readonly IReadOnlySet<string>? _updateNames;
    private readonly bool _latestInstallable;
    private readonly bool _allowCritical;
    private readonly SortedDictionary<string, SortedSet<string>> _skippedCritical = new(StringComparer.Ordinal);
    private readonly List<Requirement> _pending = [];
    private readonly Dictionary<string, ResolvedSei> _assigned = new(StringComparer.Ordinal);
    private readonly HashSet<SeiIndex> _validatedIndexes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<VersionRange>> _prereleaseHints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _prereleaseHintTexts = new(StringComparer.Ordinal);
    private SeikoException? _lastConflict;
    private long _totalAssignments;
    private bool _hintsGrew;
    private List<SeiDependency> _artifactRequirements = [];

    public Resolver(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock? existingLock,
        string rid,
        bool allowPrerelease,
        IReadOnlySet<string>? updateNames = null,
        bool latestInstallable = false,
        bool allowCritical = false)
    {
        _sources = sources;
        _lock = existingLock;
        _rid = rid;
        _allowPrerelease = allowPrerelease;
        _updateNames = updateNames;
        _latestInstallable = latestInstallable;
        _allowCritical = allowCritical;
    }

    public IReadOnlyList<string> Warnings()
    {
        var warnings = SkippedWarnings();
        warnings.AddRange(Retirement.Warnings(_assigned.Values.ToList()));
        return warnings;
    }

    private List<string> SkippedWarnings()
    {
        var warnings = new List<string>();
        foreach (var (name, versions) in _skippedCritical)
        {
            warnings.Add(
                $"skipped critical version(s) of {name}: {string.Join(", ", versions)}; pass --allow-critical to install");
        }

        return warnings;
    }

    public IReadOnlyList<ResolvedSei> Resolve(IReadOnlyList<SeiSpec> specs)
    {
        var requirements = new List<SeiDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            _artifactRequirements = requirements;
            var result = ResolveOnce(specs);
            var needed = CollectArtifactDependencies();
            if (RelationsEqual(needed, requirements))
            {
                return result;
            }

            var signature = string.Join(
                "\n",
                needed.Select(dependency => $"{dependency.Name}|{dependency.Version ?? ""}"));
            if (!seen.Add(signature))
            {
                throw Fail("artifact dependency requirements did not converge");
            }

            requirements = needed;
        }
    }

    private IReadOnlyList<ResolvedSei> ResolveOnce(IReadOnlyList<SeiSpec> specs)
    {
        var directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!SeiName.IsValid(spec.Name))
            {
                throw Fail($"invalid sei name '{spec.Name}'");
            }

            directNames.Add(spec.Name);
        }

        _totalAssignments = 0;
        while (true)
        {
            _pending.Clear();
            _assigned.Clear();
            _lastConflict = null;
            _hintsGrew = false;

            foreach (var spec in specs)
            {
                _pending.Add(CreateRequirement(spec.Name, spec.Version, null));
            }

            // Direct declarations are visible for the whole run; only hints
            // discovered while exploring dependencies require a restart.
            _hintsGrew = false;

            foreach (var dependency in _artifactRequirements)
            {
                _pending.Add(CreateRequirement(
                    dependency.Name,
                    dependency.Version,
                    "artifact dependency"));
            }

            if (Search(0))
            {
                foreach (var sei in _assigned.Values)
                {
                    sei.Direct = directNames.Contains(sei.Name);
                }

                // In latest-installable mode Search already selected the
                // artifacts for the accepted graph; only Direct flags changed.
                var missing = _latestInstallable ? null : SelectArtifacts();
                if (missing is not null)
                {
                    throw Fail(missing.Value.Message);
                }

                return _assigned.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            }

            if (!_hintsGrew)
            {
                var failure = _lastConflict ?? Fail("no version satisfies the declared ranges");
                var skipped = SkippedWarnings();
                if (skipped.Count > 0)
                {
                    throw new SeikoException(
                        $"{failure.Message} ({string.Join("; ", skipped)})",
                        failure.Code ?? "resolve");
                }

                throw failure;
            }
        }
    }

    private bool Search(int index)
    {
        if (index > MaxSearchDepth)
        {
            throw Fail($"dependency resolution exceeded the depth budget of {MaxSearchDepth}");
        }

        if (index >= _pending.Count)
        {
            if (!ValidateFinalSolution())
            {
                return false;
            }

            if (!_latestInstallable)
            {
                return true;
            }

            // Artifact availability depends on the final graph. A failure here
            // is a property of this assignment combination, not of the version
            // globally, so backtrack instead of excluding the version.
            var missing = SelectArtifacts();
            if (missing is null)
            {
                return true;
            }

            _lastConflict = Fail(missing.Value.Message);
            return false;
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

        if (!TryFindSource(requirement.Name, out var source, out var indexData))
        {
            _lastConflict = new SeikoException(
                $"sei '{requirement.Name}' not found in any source (required by {Describe(requirement.From)})",
                "resolve");
            return false;
        }

        var candidates = Candidates(requirement.Name, source, indexData, _pending);
        if (candidates.Count == 0)
        {
            _lastConflict = Conflict(requirement.Name, source.Id, _pending);
            return false;
        }

        foreach (var (entry, version) in Order(requirement.Name, candidates))
        {
            if (++_totalAssignments > MaxTotalAssignments)
            {
                throw Fail("dependency resolution exceeded the work budget");
            }

            _assigned[requirement.Name] = new ResolvedSei
            {
                Name = requirement.Name,
                ParsedVersion = version,
                Entry = entry,
                Source = source,
                PackageDeprecated = indexData.Deprecated,
            };

            var added = 0;
            foreach (var dependency in entry.Dependencies)
            {
                if (!SeiName.IsValid(dependency.Name))
                {
                    throw Fail($"invalid sei name '{dependency.Name}'");
                }

                if (string.Equals(dependency.Name, requirement.Name, StringComparison.Ordinal))
                {
                    throw Fail($"sei '{requirement.Name}' depends on itself");
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

            if (_hintsGrew)
            {
                // A deeper branch found a prerelease range this run did not
                // consider; restart with the wider hint set instead of
                // committing to a partial result.
                return false;
            }
        }

        return false;
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
            || constraints.Any(constraint => RangeAllowsPrerelease(constraint.Range));
        var candidates = new List<(SeiVersionEntry Entry, NuGetVersion Version)>();
        var seenVersions = new HashSet<NuGetVersion>();
        if (_validatedIndexes.Add(index))
        {
            SourceValidation.EnsureIndex(index, source.Id, "resolve");
        }

        foreach (var entry in index.Versions)
        {
            if (!NuGetVersion.TryParse(entry.Version, out var version))
            {
                throw Fail($"source '{source.Id}': invalid version '{entry.Version}' for {name}");
            }

            if (!seenVersions.Add(version))
            {
                throw Fail(
                    $"source '{source.Id}': duplicate version '{entry.Version}' for {name}");
            }

            if (!constraints.All(constraint => constraint.Range.Satisfies(version)))
            {
                continue;
            }

            if (version.IsPrerelease && !allowPrerelease && !HintAllows(name, version))
            {
                continue;
            }

            if (entry.Yanked && !(lockedVersion is not null && version.Equals(lockedVersion)))
            {
                if (Retirement.IsCritical(entry))
                {
                    AddSkipped(_skippedCritical, name, AdvisoryLabel(entry, entry.Version));
                }

                continue;
            }

            if (Retirement.IsCritical(entry) && !_allowCritical)
            {
                AddSkipped(_skippedCritical, name, AdvisoryLabel(entry, entry.Version));
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
                && RangeAllowsPrerelease(requirement.Range));
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

    private List<SeiDependency> CollectArtifactDependencies()
    {
        return _assigned.Values
            .SelectMany(sei => sei.Artifact.Dependencies ?? [])
            .Where(dependency => !string.IsNullOrWhiteSpace(dependency.Name))
            .GroupBy(
                dependency => (dependency.Name, dependency.Version ?? ""),
                StringTupleComparer.Instance)
            .Select(group => new SeiDependency
            {
                Name = group.Key.Name,
                Version = group.First().Version,
            })
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Version ?? "", StringComparer.Ordinal)
            .ToList();
    }

    private static bool RelationsEqual(
        IReadOnlyList<SeiDependency> left,
        IReadOnlyList<SeiDependency> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal)
                || !string.Equals(left[i].Version ?? "", right[i].Version ?? "", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Name, string Version)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Name, string Version) x, (string Name, string Version) y)
            => string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                && string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string Name, string Version) obj)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Name),
                StringComparer.Ordinal.GetHashCode(obj.Version));
    }

    private (string Name, string Version, string Message)? SelectArtifacts()
    {
        var graphNames = _assigned.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        foreach (var sei in _assigned.Values)
        {
            var scored = new List<(ArtifactEntry Artifact, int Score)>();
            foreach (var artifact in sei.Entry.Artifacts)
            {
                var score = ScoreArtifact(artifact, _rid, _assigned);
                if (score >= 0)
                {
                    scored.Add((artifact, score));
                }
            }

            if (scored.Count == 0)
            {
                return (
                    sei.Name,
                    sei.Version,
                    $"no artifact of {sei.Name} {sei.Version} matches rid '{_rid}' and sei [{string.Join(", ", graphNames)}]");
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

        return null;
    }

    internal static int ScoreArtifact(
        ArtifactEntry artifact,
        string rid,
        IReadOnlyDictionary<string, ResolvedSei> assigned)
    {
        if (artifact.Rid is not null
            && !artifact.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var score = 0;
        foreach (var prerequisite in artifact.Prerequisites ?? [])
        {
            if (!assigned.TryGetValue(prerequisite.Name, out var target))
            {
                return -1;
            }

            if (!ParseRange(prerequisite.Version, prerequisite.Name).Satisfies(target.ParsedVersion))
            {
                return -1;
            }

            score++;
        }

        foreach (var peer in artifact.OptionalPeers ?? [])
        {
            if (assigned.TryGetValue(peer.Name, out var target)
                && !ParseRange(peer.Version, peer.Name).Satisfies(target.ParsedVersion))
            {
                return -1;
            }
        }

        foreach (var conflict in artifact.Conflicts ?? [])
        {
            if (assigned.TryGetValue(conflict.Name, out var target)
                && ParseRange(conflict.Version, conflict.Name).Satisfies(target.ParsedVersion))
            {
                return -1;
            }
        }

        return score;
    }

    private bool TryFindSource(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISeiSource? found,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SeiIndex? index)
    {
        foreach (var source in _sources)
        {
            index = source.GetSei(name);
            if (index is not null)
            {
                found = source;
                return true;
            }
        }

        found = null;
        index = null;
        return false;
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
        if (version is not null && RangeAllowsPrerelease(range))
        {
            if (!_prereleaseHintTexts.TryGetValue(name, out var texts))
            {
                texts = new HashSet<string>(StringComparer.Ordinal);
                _prereleaseHintTexts[name] = texts;
            }

            if (texts.Add(version))
            {
                if (!_prereleaseHints.TryGetValue(name, out var ranges))
                {
                    ranges = [];
                    _prereleaseHints[name] = ranges;
                }

                ranges.Add(range);
                _hintsGrew = true;
            }
        }

        return new Requirement(name, range, from);
    }

    private bool ValidateFinalSolution()
    {
        foreach (var (name, resolved) in _assigned)
        {
            if (!resolved.ParsedVersion.IsPrerelease || _allowPrerelease)
            {
                continue;
            }

            var authorized = _pending.Any(requirement =>
                string.Equals(requirement.Name, name, StringComparison.Ordinal)
                && RangeAllowsPrerelease(requirement.Range));
            if (!authorized)
            {
                _lastConflict = new SeikoException(
                    $"prerelease {name} {resolved.Version} is not required by the resolved graph",
                    "resolve");
                return false;
            }
        }

        foreach (var (name, resolved) in _assigned)
        {
            if (!ValidateRelations(name, resolved, resolved.Entry.Prerequisites, requirePresent: true, "prerequisite"))
            {
                return false;
            }

            if (!ValidateRelations(name, resolved, resolved.Entry.OptionalPeers, requirePresent: false, "optional peer"))
            {
                return false;
            }

            if (!ValidateConflicts(name, resolved, resolved.Entry.Conflicts))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateRelations(
        string owner,
        ResolvedSei resolved,
        List<SeiDependency>? relations,
        bool requirePresent,
        string label)
    {
        foreach (var relation in relations ?? [])
        {
            if (!_assigned.TryGetValue(relation.Name, out var target))
            {
                if (!requirePresent)
                {
                    continue;
                }

                _lastConflict = Fail(
                    $"prerequisite '{relation.Name}' required by {owner} {resolved.Version} is not in the resolved graph; add it explicitly or through a dependency");
                return false;
            }

            var range = ParseRange(relation.Version, relation.Name);
            if (!range.Satisfies(target.ParsedVersion))
            {
                _lastConflict = Fail(
                    $"{label} '{relation.Name}' for {owner} {resolved.Version} is {target.Version}, which does not satisfy {relation.Version ?? "*"}");
                return false;
            }
        }

        return true;
    }

    private bool ValidateConflicts(
        string owner,
        ResolvedSei resolved,
        List<SeiDependency>? conflicts)
    {
        foreach (var conflict in conflicts ?? [])
        {
            if (!_assigned.TryGetValue(conflict.Name, out var target))
            {
                continue;
            }

            if (ParseRange(conflict.Version, conflict.Name).Satisfies(target.ParsedVersion))
            {
                _lastConflict = Fail(
                    $"conflict: {owner} {resolved.Version} cannot coexist with {conflict.Name} {target.Version}");
                return false;
            }
        }

        return true;
    }

    private bool HintAllows(string name, NuGetVersion version)
        => _prereleaseHints.TryGetValue(name, out var ranges)
            && ranges.Any(range => range.Satisfies(version));

    private static bool RangeAllowsPrerelease(VersionRange range)
        => range.MinVersion?.IsPrerelease == true
            || range.MaxVersion?.IsPrerelease == true
            || range.Float is { IncludePrerelease: true };

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

    private static void AddSkipped(
        SortedDictionary<string, SortedSet<string>> target,
        string name,
        string version)
    {
        if (!target.TryGetValue(name, out var versions))
        {
            versions = new SortedSet<string>(StringComparer.Ordinal);
            target[name] = versions;
        }

        versions.Add(version);
    }

    private static string AdvisoryLabel(SeiVersionEntry entry, string version)
        => string.IsNullOrWhiteSpace(entry.Advisory?.Url)
            ? version
            : $"{version} ({entry.Advisory.Url})";

    private sealed record Requirement(
        string Name,
        VersionRange Range,
        string? From);
}
