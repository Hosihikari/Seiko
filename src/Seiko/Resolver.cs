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
    private readonly Dictionary<string, ForcedPick> _forced;
    private readonly bool _checkAmbiguity;
    private readonly SearchBudget _budget;
    private readonly Action<IReadOnlyList<ResolvedSei>>? _onSolution;
    private readonly bool _collectAll;
    private IReadOnlyList<SeiSpec> _specs = [];
    private HashSet<string> _directNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VersionRange>> _unknownConstraints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<VersionRange>> _unknownExcluded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _shadowed = new(StringComparer.Ordinal);
    private SeikoException? _lastConflict;
    private bool _hintsGrew;
    private bool _exhausted;
    private List<IReadOnlyList<ResolvedSei>>? _collected;

    public Resolver(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock? existingLock,
        string rid,
        bool allowPrerelease,
        IReadOnlySet<string>? updateNames = null,
        bool latestInstallable = false,
        bool allowCritical = false,
        Action<IReadOnlyList<ResolvedSei>>? onSolution = null,
        bool collectAll = false)
        : this(
            sources,
            existingLock,
            rid,
            allowPrerelease,
            updateNames,
            latestInstallable,
            allowCritical,
            new Dictionary<string, ForcedPick>(StringComparer.Ordinal),
            checkAmbiguity: true,
            new SearchBudget(),
            onSolution,
            collectAll)
    {
    }

    private Resolver(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock? existingLock,
        string rid,
        bool allowPrerelease,
        IReadOnlySet<string>? updateNames,
        bool latestInstallable,
        bool allowCritical,
        Dictionary<string, ForcedPick> forced,
        bool checkAmbiguity,
        SearchBudget budget,
        Action<IReadOnlyList<ResolvedSei>>? onSolution = null,
        bool collectAll = false,
        List<IReadOnlyList<ResolvedSei>>? collected = null)
    {
        _sources = sources;
        _lock = existingLock;
        _rid = rid;
        _allowPrerelease = allowPrerelease;
        _updateNames = updateNames;
        _latestInstallable = latestInstallable;
        _allowCritical = allowCritical;
        _forced = forced;
        _checkAmbiguity = checkAmbiguity;
        _budget = budget;
        _onSolution = onSolution;
        _collectAll = collectAll;
        _collected = collected;
    }

    public IReadOnlyList<string> Warnings()
    {
        var warnings = SkippedWarnings();
        warnings.AddRange(Retirement.Warnings(_assigned.Values.ToList()));
        return warnings;
    }

    // True when a search limit stopped the walk: parts of the graph may never
    // have been visited, so a caller collecting gaps cannot call its list
    // complete. The limits live on the shared budget so a child probe's
    // exhaustion counts too.
    public bool Incomplete => _budget.Exhausted || _budget.DepthSpent;

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
        _specs = specs;
        // A collecting run wants every graph it reaches, not only the answer:
        // the sources it read are the gaps somebody has to fill. Every leaf
        // solution is reported as soon as it is found, so an optional set that
        // cannot be solved is still measured up to the point it fails.
        _collected = _collectAll ? _collected ?? [] : null;
        try
        {
            return ResolveWithPreference(specs);
        }
        finally
        {
            _collected = null;
        }
    }

    private IReadOnlyList<ResolvedSei> ResolveWithPreference(IReadOnlyList<SeiSpec> specs)
    {
        var result = AcceptAfterProbeErrors(ResolveOnce(specs));
        if (!_checkAmbiguity)
        {
            return result;
        }

        // Specificity is a selection rule, not only an ambiguity test: when
        // exactly one artifact is solvable at the highest specificity, the
        // final answer must use it. Each round can only add a force, and a
        // force is re-checked by a full search, so this settles quickly.
        for (var round = 0; round < 64; round++)
        {
            var added = PreferMoreSpecificArtifact(result);
            if (added.Count == 0)
            {
                return AcceptAfterProbeErrors(result);
            }

            try
            {
                result = ResolveOnce(specs);
            }
            catch (SeikoException)
            {
                // The preference does not survive the other forced choices;
                // fall back to the solution that was already complete.
                foreach (var name in added)
                {
                    _forced.Remove(name);
                }

                return AcceptAfterProbeErrors(ResolveOnce(specs));
            }
        }

        return AcceptAfterProbeErrors(result);
    }

    // A probe error met while judging an artifact does not settle that
    // artifact; if the answer is accepted anyway, the error has to reach the
    // caller rather than be dropped with the probe.
    private IReadOnlyList<ResolvedSei> AcceptAfterProbeErrors(IReadOnlyList<ResolvedSei> result)
    {
        if (_probeError is not null)
        {
            throw _probeError;
        }

        return result;
    }

    // Force the unique highest-specificity solvable artifact of each package
    // where the current answer is less specific. Returns the names forced.
    private List<string> PreferMoreSpecificArtifact(IReadOnlyList<ResolvedSei> result)
    {
        var added = new List<string>();
        foreach (var resolved in result.OrderBy(sei => sei.Name, StringComparer.Ordinal))
        {
            if (_forced.ContainsKey(resolved.Name))
            {
                continue;
            }

            var ridArtifacts = resolved.Entry.Artifacts
                .Where(artifact => string.IsNullOrWhiteSpace(artifact.Rid)
                    || artifact.Rid.Equals(_rid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ridArtifacts.Count < 2)
            {
                continue;
            }

            var solvable = ridArtifacts
                .Where(artifact => ReferenceEquals(artifact, resolved.Artifact)
                    || CanSolveWith(artifact, resolved))
                .ToList();
            var top = solvable.Max(artifact => artifact.Prerequisites?.Count ?? 0);
            var best = solvable
                .Where(artifact => (artifact.Prerequisites?.Count ?? 0) == top)
                .ToList();
            if (best.Count != 1
                || (resolved.Artifact.Prerequisites?.Count ?? 0) >= top)
            {
                continue;
            }

            _forced[resolved.Name] = new ForcedPick(resolved.Version, best[0]);
            added.Add(resolved.Name);
        }

        return added;
    }

    private IReadOnlyList<ResolvedSei> ResolveOnce(IReadOnlyList<SeiSpec> specs)
    {
        var directNames = _directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (!SeiName.IsValid(spec.Name))
            {
                throw Fail($"invalid sei name '{spec.Name}'");
            }

            directNames.Add(spec.Name);
        }

        while (true)
        {
            _pending.Clear();
            _assigned.Clear();
            _lastConflict = null;
            _hintsGrew = false;
            _exhausted = false;

            foreach (var spec in specs)
            {
                _pending.Add(CreateRequirement(spec.Name, spec.Version, null));
            }

            // Direct declarations are visible for the whole run; only hints
            // discovered while exploring dependencies require a restart.
            _hintsGrew = false;

            if (Search(0))
            {
                return Answer(directNames);
            }

            // A guessed leaf never ends the walk, so "no solution" here is not
            // a stop signal: every graph reached was already reported and the
            // first one is the answer. A grown hint set still wins, because a
            // restart may widen the candidates this pass never saw.
            if (!_hintsGrew && _collectAll && _collected is { Count: > 0 })
            {
                return _collected[0].ToList();
            }

            if (!_hintsGrew)
            {
                // The whole search finished without a solution; any error that
                // leaves earlier (a source read, an index check, a listener)
                // is not this answer.
                _exhausted = true;
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

    // Record the current leaf: a snapshot, because the artifact loop mutates
    // the assignment it came from, and the probe wants a stable graph.
    private void Report()
    {
        // Assignment order, not name order: the cross-source check in the
        // real run walks the graph the way it was built, and its first
        // failure decides which later reads never happen.
        var answer = _assigned.Values
            .Select(sei => new ResolvedSei
            {
                Name = sei.Name,
                ParsedVersion = sei.ParsedVersion,
                Entry = sei.Entry,
                Source = sei.Source,
                Direct = _directNames.Contains(sei.Name),
                Artifact = sei.Artifact,
                PackageDeprecated = sei.PackageDeprecated,
            })
            .ToList();
        // The listener's verdict is part of the leaf's verdict: if it fails,
        // the failure leaves this search. A child probe catches it at
        // CanSolveWith and calls the artifact unsolvable; the main search
        // lets it reach the caller. Only leaves that passed are collected.
        _onSolution?.Invoke(answer);
        _collected?.Add(answer);
    }

    // Constraint bookkeeping is part of the branch, so it is saved here and
    // restored when the branch below fails. A plain deep copy is enough; the
    // searches are small and this only exists in collecting runs.
    private (Dictionary<string, List<VersionRange>> Keep, Dictionary<string, List<VersionRange>> Excluded,
        HashSet<string> Shadowed) Snapshot()
        => (Copy(_unknownConstraints), Copy(_unknownExcluded), new HashSet<string>(_shadowed, StringComparer.Ordinal));

    private static Dictionary<string, List<VersionRange>> Copy(
        Dictionary<string, List<VersionRange>> source)
        => source.ToDictionary(
            pair => pair.Key,
            pair => new List<VersionRange>(pair.Value),
            StringComparer.Ordinal);

    private void Restore(
        (Dictionary<string, List<VersionRange>> Keep, Dictionary<string, List<VersionRange>> Excluded,
            HashSet<string> Shadowed) snapshot)
    {
        Replace(_unknownConstraints, snapshot.Keep);
        Replace(_unknownExcluded, snapshot.Excluded);
        _shadowed.Clear();
        _shadowed.UnionWith(snapshot.Shadowed);
    }

    private static void Replace(
        Dictionary<string, List<VersionRange>> target,
        Dictionary<string, List<VersionRange>> source)
    {
        // The lists are copied, not shared: a later Add on the working state
        // would otherwise write through into the snapshot and survive the
        // next restore.
        target.Clear();
        foreach (var (name, ranges) in source)
        {
            target[name] = new List<VersionRange>(ranges);
        }
    }

    // The known ranges on an untrusted name, created on first use.
    private List<VersionRange> Constraints(string name)
        => _unknownConstraints.TryGetValue(name, out var list)
            ? list
            : _unknownConstraints[name] = [];

    // A name whose assignment cannot be trusted: an invented placeholder, or
    // an answer from a later source while an earlier one could not be read.
    private bool Uncertain(string name)
        => _assigned.TryGetValue(name, out var sei) && sei.Entry.ProbeUnknown
            || _shadowed.Contains(name);

    // Whether the known ranges leave any version once the exclusions from
    // conflicts are cut away. Only a proven-empty remainder counts; a partial
    // overlap stays a guess. Several exclusions may cover between them what
    // none covers alone, so the union is subtracted, not matched one by one.
    private bool Contradicts(string name)
    {
        if (!_unknownConstraints.TryGetValue(name, out var ranges) || ranges.Count == 0)
        {
            return false;
        }

        var common = VersionRange.CommonSubSet(ranges);
        if (Empty(common))
        {
            return true;
        }

        if (!_unknownExcluded.TryGetValue(name, out var excluded) || excluded.Count == 0)
        {
            return false;
        }

        return Covered(common, excluded);
    }

    // Whether the exclusions between them leave no version of the allowed
    // interval. The interval list is cut by each exclusion in turn, so a
    // union of several narrow exclusions covers exactly like one wide one.
    private static bool Covered(VersionRange allowed, IReadOnlyList<VersionRange> excluded)
    {
        var left = new List<(NuGetVersion? Lo, bool LoInc, NuGetVersion? Hi, bool HiInc)>
        {
            (allowed.MinVersion, allowed.IsMinInclusive, allowed.MaxVersion, allowed.IsMaxInclusive),
        };

        foreach (var range in excluded)
        {
            var next = new List<(NuGetVersion? Lo, bool LoInc, NuGetVersion? Hi, bool HiInc)>();
            foreach (var (lo, loInc, hi, hiInc) in left)
            {
                Cut(lo, loInc, hi, hiInc, range, next);
            }

            left = next;
            if (left.Count == 0)
            {
                return true;
            }
        }

        return left.Count == 0;
    }

    // Subtract one exclusion from one interval, appending what is left. A
    // disjoint exclusion leaves the interval untouched; only the overlap is
    // cut out, so the pieces are the interval's own ends. A null bound is
    // unbounded on that side.
    private static void Cut(
        NuGetVersion? lo,
        bool loInc,
        NuGetVersion? hi,
        bool hiInc,
        VersionRange cut,
        List<(NuGetVersion? Lo, bool LoInc, NuGetVersion? Hi, bool HiInc)> output)
    {
        var cLo = cut.MinVersion;
        var cHi = cut.MaxVersion;

        // Disjoint: the exclusion lies wholly before or after the interval,
        // or touches it only at a point neither side includes.
        var disjoint =
            (hi is not null && cLo is not null
                && (cLo > hi || (cLo.Equals(hi) && !(hiInc && cut.IsMinInclusive))))
            || (lo is not null && cHi is not null
                && (cHi < lo || (cHi.Equals(lo) && !(loInc && cut.IsMaxInclusive))));
        if (disjoint)
        {
            Add(lo, loInc, hi, hiInc, output);
            return;
        }

        // The overlap is the tighter of the two on each side.
        var oLo = lo is not null && (cLo is null || lo > cLo) ? lo : cLo;
        var oLoInc = ReferenceEquals(oLo, lo) ? loInc : cut.IsMinInclusive;
        var oHi = hi is not null && (cHi is null || hi < cHi) ? hi : cHi;
        var oHiInc = ReferenceEquals(oHi, hi) ? hiInc : cut.IsMaxInclusive;

        // Left of the overlap.
        if (oLo is not null)
        {
            Add(lo, loInc, oLo, !oLoInc, output);
        }

        // Right of it.
        if (oHi is not null)
        {
            Add(oHi, !oHiInc, hi, hiInc, output);
        }
    }

    private static void Add(
        NuGetVersion? lo,
        bool loInc,
        NuGetVersion? hi,
        bool hiInc,
        List<(NuGetVersion? Lo, bool LoInc, NuGetVersion? Hi, bool HiInc)> output)
    {
        if (lo is not null && hi is not null)
        {
            if (lo > hi || (lo.Equals(hi) && !(loInc && hiInc)))
            {
                return;
            }
        }

        output.Add((lo, loInc, hi, hiInc));
    }

    // An empty intersection is the degenerate `(0.0.0, 0.0.0)` — a singleton
    // with both ends open. An unbounded answer has no bounds at all, and a
    // real singleton is inclusive.
    private static bool Empty(VersionRange range)
        => range.MinVersion is { } min
            && range.MaxVersion is { } max
            && min.Equals(max)
            && !range.IsMinInclusive
            && !range.IsMaxInclusive;

    // Would this artifact certainly lose against the untrusted names already
    // in the graph? The real run drops it in CanStillMatch as soon as any X is
    // in hand, so its dependencies never join the search. Prerequisite, peer
    // and conflict ranges all narrow the same names, so the whole set is
    // applied at once instead of one range at a time.
    private bool CannotMatch(ArtifactEntry artifact)
    {
        var requirements = new List<(string Name, VersionRange Range, bool Keep)>();
        foreach (var relation in (artifact.Prerequisites ?? [])
            .Concat(artifact.OptionalPeers ?? []))
        {
            requirements.Add((relation.Name, ParseRange(relation.Version, relation.Name), true));
        }

        foreach (var relation in artifact.Conflicts ?? [])
        {
            requirements.Add((relation.Name, ParseRange(relation.Version, relation.Name), false));
        }

        foreach (var name in requirements
            .Select(item => item.Name)
            .Where(Uncertain)
            .Distinct(StringComparer.Ordinal))
        {
            var kept = Constraints(name).ToList();
            var excluded = _unknownExcluded.TryGetValue(name, out var trimmed)
                ? trimmed.ToList()
                : [];
            foreach (var item in requirements.Where(item => item.Name == name))
            {
                (item.Keep ? kept : excluded).Add(item.Range);
            }

            var allowed = VersionRange.CommonSubSet(kept);
            if (Empty(allowed) || (excluded.Count > 0 && Covered(allowed, excluded)))
            {
                return true;
            }
        }

        return false;
    }

    // The accepted graph, in lock order. A collecting run also reports it
    // here: in that mode a found leaf does not end the search, so the answer
    // the caller gets is the first one found.
    private List<ResolvedSei> Answer(HashSet<string> directNames)
    {
        foreach (var sei in _assigned.Values)
        {
            sei.Direct = directNames.Contains(sei.Name);
        }

        // Verification first, in build order; only the result is ordered by
        // name for display, so both runs judge the same reads in the same
        // order. A collecting run leaves the check to its listener: the
        // resolver only ever sees one aggregate source, so the comparison
        // belongs to whoever built that aggregate.
        if (!_collectAll)
        {
            CrossSourceCheck.Verify(_sources, _assigned.Values, "checksum");
        }
        return _assigned.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private bool Search(int index)
    {
        if (index > MaxSearchDepth)
        {
            _budget.DepthSpent = true;
            throw Fail($"dependency resolution exceeded the depth budget of {MaxSearchDepth}");
        }

        if (index >= _pending.Count)
        {
            var solved = ValidateFinalSolution();
            if (!solved || !_collectAll)
            {
                return solved;
            }

            Report();
            // The normal run would stop here, and it may only when the leaf is
            // proven. Two things make it a guess: a placeholder, which
            // answers for its name where the real version could not, and a
            // name taken from a later source after an earlier one could not
            // be read — that earlier source may well have a different answer
            // and wins. Keep walking until neither remains, or the
            // alternatives run out.
            return !_assigned.Values.Any(sei =>
                sei.Entry.ProbeUnknown || _shadowed.Contains(sei.Name));
        }

        var requirement = _pending[index];
        if (!SeiName.IsValid(requirement.Name))
        {
            throw Fail($"invalid sei name '{requirement.Name}'");
        }

        if (_assigned.TryGetValue(requirement.Name, out var existing))
        {
            if (Uncertain(requirement.Name))
            {
                // The version is invented, so the range is not judged against
                // it — but two known ranges can still contradict each other,
                // and then no answer for this name could satisfy both. That is
                // the one thing already-cached metadata proves, so it has to
                // fail the branch and send the search to another one.
                var ranges = Constraints(requirement.Name);
                ranges.Add(requirement.Range);
                if (Contradicts(requirement.Name))
                {
                    _lastConflict = new SeikoException(
                        $"version ranges for {requirement.Name} contradict each other ({string.Join(", ", ranges.Distinct())}); "
                        + $"latest from {Describe(requirement.From)}",
                        "resolve");
                    return false;
                }

                return Search(index + 1);
            }

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
        if (_forced.TryGetValue(requirement.Name, out var forcedVersion))
        {
            candidates = candidates
                .Where(candidate => candidate.Version.ToNormalizedString() == forcedVersion.Version)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            _lastConflict = Conflict(requirement.Name, source.Id, _pending);
            return false;
        }

        foreach (var (entry, version) in Order(requirement.Name, candidates))
        {
            if (!_budget.TryConsume())
            {
                throw Fail("dependency resolution exceeded the work budget");
            }

            // A placeholder is a guess pending the cache; nothing about it may
            // make the walk stop, or the remaining gaps would go unreported.
            // See OfflineMetadata.ProbeSource.
            var resolved = new ResolvedSei
            {
                Name = requirement.Name,
                ParsedVersion = version,
                Entry = entry,
                Source = source,
                PackageDeprecated = indexData.Deprecated,
            };
            _assigned[requirement.Name] = resolved;
            if (indexData.ProbeShadowed)
            {
                _shadowed.Add(requirement.Name);
            }

            if (entry.ProbeUnknown || indexData.ProbeShadowed)
            {
                // Everything already pending for the name is a known range
                // too; the untrusted answer satisfies all of them at once,
                // which is exactly the over-fit this guards against. It holds
                // for a shadowed answer as much as for a placeholder.
                _unknownConstraints[requirement.Name] = _pending
                    .Where(item => string.Equals(item.Name, requirement.Name, StringComparison.Ordinal))
                    .Select(item => item.Range)
                    .Concat(_unknownExcluded.TryGetValue(requirement.Name, out var excluded)
                        ? excluded
                        : [])
                    .Distinct()
                    .ToList();
            }

            // The version's own uncertainty has been recorded by now, so an
            // artifact backtrack restores to "this version, still uncertain"
            // with its whole known range set — not to before the name was
            // assigned at all.
            var constraintsBefore = Snapshot();

            var pendingBeforeVersion = _pending.Count;
            AddDependencies(entry.Dependencies, requirement.Name, requirement.Name);
            var ridArtifacts = entry.Artifacts
                .Where(artifact => string.IsNullOrWhiteSpace(artifact.Rid)
                    || artifact.Rid.Equals(_rid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ridArtifacts.Count == 0)
            {
                _lastConflict = Fail(NoArtifactMessage(resolved));
                _pending.RemoveRange(pendingBeforeVersion, _pending.Count - pendingBeforeVersion);
                _assigned.Remove(requirement.Name);
                Restore(constraintsBefore);
                if (_latestInstallable)
                {
                    continue;
                }

                throw _lastConflict;
            }

            var artifactCandidates = ridArtifacts
                .Where(artifact => CanStillMatch(artifact, resolved))
                // An artifact already proven dead against a placeholder is
                // dropped before its dependencies join the search, exactly
                // where the real run drops it once the version is in hand.
                .Where(artifact => !CannotMatch(artifact))
                .ToList();

            // A probe pins the sibling it is testing, so the search must not
            // silently fall back to the other one. Sources hand out fresh
            // index objects per read, so the artifact is matched by identity.
            if (_forced.TryGetValue(requirement.Name, out var forced))
            {
                artifactCandidates = artifactCandidates
                    .Where(artifact => SameArtifact(artifact, forced)
                        && string.Equals(
                            version.ToNormalizedString(),
                            forced.Version,
                            StringComparison.Ordinal))
                    .ToList();
            }

            if (artifactCandidates.Count == 0)
            {
                _lastConflict = Fail(NoArtifactMessage(resolved));
                _pending.RemoveRange(pendingBeforeVersion, _pending.Count - pendingBeforeVersion);
                _assigned.Remove(requirement.Name);
                Restore(constraintsBefore);
                continue;
            }

            var found = false;
            foreach (var artifact in OrderArtifacts(requirement.Name, artifactCandidates))
            {
                if (!_budget.TryConsume())
                {
                    throw Fail("dependency resolution exceeded the work budget");
                }

                resolved.Artifact = artifact;
                var pendingBeforeArtifact = _pending.Count;
                AddDependencies(
                    artifact.Dependencies,
                    requirement.Name,
                    $"{requirement.Name} (artifact)");
                if (Search(index + 1))
                {
                    found = true;
                    break;
                }

                _pending.RemoveRange(
                    pendingBeforeArtifact,
                    _pending.Count - pendingBeforeArtifact);
                Restore(constraintsBefore);
                if (_hintsGrew)
                {
                    break;
                }
            }

            if (found)
            {
                return true;
            }

            _pending.RemoveRange(
                pendingBeforeVersion,
                _pending.Count - pendingBeforeVersion);
            _assigned.Remove(requirement.Name);
            Restore(constraintsBefore);
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

    private void AddDependencies(
        List<SeiDependency>? dependencies,
        string owner,
        string description)
    {
        foreach (var dependency in dependencies ?? [])
        {
            if (!SeiName.IsValid(dependency.Name))
            {
                throw Fail($"invalid sei name '{dependency.Name}'");
            }

            if (string.Equals(dependency.Name, owner, StringComparison.Ordinal))
            {
                throw Fail($"sei '{owner}' depends on itself");
            }

            _pending.Add(CreateRequirement(dependency.Name, dependency.Version, description));
        }
    }

    private bool CanStillMatch(ArtifactEntry artifact, ResolvedSei owner)
    {
        foreach (var prerequisite in artifact.Prerequisites ?? [])
        {
            if (_assigned.TryGetValue(prerequisite.Name, out var target)
                && !Uncertain(prerequisite.Name)
                && !ParseRange(prerequisite.Version, prerequisite.Name)
                    .Satisfies(target.ParsedVersion))
            {
                return false;
            }
        }

        foreach (var peer in artifact.OptionalPeers ?? [])
        {
            if (_assigned.TryGetValue(peer.Name, out var target)
                && !Uncertain(peer.Name)
                && !ParseRange(peer.Version, peer.Name).Satisfies(target.ParsedVersion))
            {
                return false;
            }
        }

        foreach (var conflict in artifact.Conflicts ?? [])
        {
            if (_assigned.TryGetValue(conflict.Name, out var target)
                && !Uncertain(conflict.Name)
                && ParseRange(conflict.Version, conflict.Name).Satisfies(target.ParsedVersion))
            {
                return false;
            }
        }

        return true;
    }

    private List<ArtifactEntry> OrderArtifacts(
        string name,
        List<ArtifactEntry> artifacts)
    {
        var lockedSha = GetLockedArtifactSha(name);
        return artifacts
            .Select((artifact, index) => (Artifact: artifact, Index: index))
            .OrderByDescending(candidate => candidate.Artifact.Prerequisites?.Count ?? 0)
            .ThenByDescending(candidate => lockedSha is not null
                && string.Equals(
                    candidate.Artifact.Source.Sha256,
                    lockedSha,
                    StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Artifact)
            .ToList();
    }

    private string NoArtifactMessage(ResolvedSei resolved)
    {
        var graphNames = _assigned.Keys.OrderBy(name => name, StringComparer.Ordinal);
        return $"no artifact of {resolved.Name} {resolved.Version} matches rid '{_rid}' and sei [{string.Join(", ", graphNames)}]";
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

            // A placeholder stands in for metadata the offline probe could not
            // read; its version is invented, so no admission check applies to
            // it. The same holds for the answer of a later source while an
            // earlier one is unread: that earlier source's version is the one
            // the real run would take, and it may sit elsewhere in the range
            // (see OfflineMetadata.ProbeSource).
            if (entry.ProbeUnknown || index.ProbeShadowed)
            {
                candidates.Add((entry, version));
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

    private string? GetLockedArtifactSha(string name)
    {
        if (_updateNames?.Contains(name) == true)
        {
            return null;
        }

        return _lock?.Sei
            .FirstOrDefault(sei => string.Equals(sei.Name, name, StringComparison.Ordinal))
            ?.Artifact.Sha256;
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

            if (Uncertain(name))
            {
                // The prerelease label belongs to the cached answer; the
                // earlier source that could not be read may well serve a
                // stable version and win. So the label cannot disqualify it.
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

            if (!ValidateConflicts(name, resolved, resolved.Entry.Conflicts, "conflict"))
            {
                return false;
            }

            if (!ValidateRelations(
                name,
                resolved,
                resolved.Artifact.Prerequisites,
                requirePresent: true,
                "artifact prerequisite"))
            {
                return false;
            }

            if (!ValidateRelations(
                name,
                resolved,
                resolved.Artifact.OptionalPeers,
                requirePresent: false,
                "artifact optional peer"))
            {
                return false;
            }

            if (!ValidateConflicts(
                name,
                resolved,
                resolved.Artifact.Conflicts,
                "artifact conflict"))
            {
                return false;
            }
        }

        EnsureNoAmbiguousArtifacts();
        return true;
    }

    private void EnsureNoAmbiguousArtifacts()
    {
        // Only the ambiguity verdict belongs to the search. Choosing the sole
        // most specific candidate happens in Resolve, after the accepted
        // solution is known, so a probe must not recurse into selection.
        if (!_checkAmbiguity)
        {
            return;
        }

        foreach (var resolved in _assigned.Values)
        {
            var ridArtifacts = resolved.Entry.Artifacts
                .Where(artifact => string.IsNullOrWhiteSpace(artifact.Rid)
                    || artifact.Rid.Equals(_rid, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ridArtifacts.Count < 2)
            {
                continue;
            }

            // Every candidate is judged in its own complete graph. A
            // prerequisite that only holds under a different dependency
            // version is still a real alternative, so it must not be filtered
            // out against the current assignment first.
            var verdicts = ridArtifacts
                .Select(artifact => ReferenceEquals(artifact, resolved.Artifact)
                    ? (Artifact: artifact, Verdict: SolveVerdict.Solvable)
                    : (Artifact: artifact, Verdict: TrySolveWith(artifact, resolved)))
                .Where(item => item.Verdict != SolveVerdict.Unsolvable)
                .ToList();
            var solvable = verdicts.Select(item => item.Artifact).ToList();
            if (solvable.Count < 2)
            {
                continue;
            }

            var top = solvable.Max(artifact => artifact.Prerequisites?.Count ?? 0);
            var tied = solvable.Count(artifact => (artifact.Prerequisites?.Count ?? 0) == top);
            if (tied < 2)
            {
                continue;
            }

            // With a guessed node in the graph, "solvable" was judged against
            // placeholder answers: the real resolution may not even reach this
            // version or this name. That is a gap answer, not an ambiguity
            // verdict — the fill-in run decides it. A certain graph keeps the
            // verdict, and so does one whose alternatives were all proven: a
            // "maybe" alone means only the fill-in run can settle the tie.
            if (_assigned.Values.Any(sei => sei.Entry.ProbeUnknown || _shadowed.Contains(sei.Name)))
            {
                continue;
            }

            // Only the tied top layer decides the verdict; a lower, unknown
            // candidate cannot change whether the top is ambiguous.
            if (verdicts.Any(item =>
                (item.Artifact.Prerequisites?.Count ?? 0) == top
                && item.Verdict == SolveVerdict.Maybe))
            {
                continue;
            }

            // An error met while probing says the tie was judged on partial
            // evidence; it is raised beside the ambiguity.
            if (_probeError is not null)
            {
                throw new SeikoException(
                    $"ambiguous artifact for {resolved.Name} {resolved.Version}: {tied} artifacts match at specificity {top}; "
                    + $"also: {_probeError.Message}",
                    "resolve",
                    _probeError);
            }

            throw Fail(
                $"ambiguous artifact for {resolved.Name} {resolved.Version}: {tied} artifacts match at specificity {top}");
        }
    }

    // Ask the real search whether swapping this artifact in still yields a
    // solution. A local existence check is not enough: the alternative can
    // introduce its own conflicts, missing transitive targets or an
    // unsatisfiable prerequisite, and it must not create a false ambiguity.
    private bool CanSolveWith(ArtifactEntry artifact, ResolvedSei owner)
        => TrySolveWith(artifact, owner) == SolveVerdict.Solvable;

    // Solvable only when the artifact's own graph is proven: a probe that
    // had to guess is a maybe, not an answer.
    private SolveVerdict TrySolveWith(ArtifactEntry artifact, ResolvedSei owner)
    {
        var forced = new Dictionary<string, ForcedPick>(_forced, StringComparer.Ordinal)
        {
            [owner.Name] = new ForcedPick(owner.Version, artifact),
        };
        // The child explores alternative artifacts; its reads are real reads
        // of the same sources, so it reports gaps to the same listener and
        // shares the budget. Its collected leaves stay its own, though: a
        // graph the parent found does not prove that this artifact is
        // solvable.
        var probe = new Resolver(
            _sources,
            _lock,
            _rid,
            _allowPrerelease,
            _updateNames,
            _latestInstallable,
            _allowCritical,
            forced,
            checkAmbiguity: false,
            _budget,
            _onSolution,
            _collectAll,
            _collectAll ? [] : null);
        try
        {
            probe.ResolveOnce(_specs);
            // A proven leaf ends the walk with the assignment in place. The
            // exhausted-search fallback (`_collected`) only exists because
            // every leaf was a guess.
            if (probe._assigned.Count == 0)
            {
                return SolveVerdict.Maybe;
            }

            // A graph holding placeholders or shadowed answers was reached by
            // guessing; the real fill-in run may land elsewhere.
            return probe._assigned.Values.Any(sei => probe.Uncertain(sei.Name))
                ? SolveVerdict.Maybe
                : SolveVerdict.Solvable;
        }
        catch (SeikoException ex)
        {
            // Only a search that ran to completion proves the artifact dead.
            // A source read, an index check or the leaf listener can fail
            // without saying anything about this artifact; such an error is
            // kept so the caller can surface it instead of calling the
            // question settled. A spent budget or depth limit is a maybe.
            // A digest disagreement is about this artifact's own bytes: the
            // mirror could not confirm them, so it is not a usable choice.
            if (ex.Code == "checksum")
            {
                return SolveVerdict.Unsolvable;
            }

            // Anything else is about the cache, not the artifact: keep the
            // error so the caller can surface it, and leave the question open.
            if (!probe._exhausted && !(_budget.Exhausted || _budget.DepthSpent))
            {
                _probeError = ex;
            }

            return probe._exhausted ? SolveVerdict.Unsolvable : SolveVerdict.Maybe;
        }
    }

    // The first real error a probe met, if any. It says nothing about the
    // artifact, so it has to reach the caller rather than be reported as a
    // verdict.
    private SeikoException? _probeError;

    private enum SolveVerdict
    {
        Unsolvable,
        Maybe,
        Solvable,
    }

    // Sources return a fresh object graph per read, so a forced artifact is
    // identified by its content-addressed digest and its place among equals.
    private static bool SameArtifact(ArtifactEntry candidate, ForcedPick forced)
        => string.Equals(
                candidate.Source?.Sha256,
                forced.Artifact.Source?.Sha256,
                StringComparison.OrdinalIgnoreCase)
            && SameRelations(candidate.Dependencies, forced.Artifact.Dependencies)
            && SameRelations(candidate.Prerequisites, forced.Artifact.Prerequisites)
            && SameRelations(candidate.OptionalPeers, forced.Artifact.OptionalPeers)
            && SameRelations(candidate.Conflicts, forced.Artifact.Conflicts);

    private static bool SameRelations(List<SeiDependency>? left, List<SeiDependency>? right)
        => (left ?? []).Count == (right ?? []).Count
            && (left ?? []).Zip(right ?? []).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && string.Equals(pair.First.Version, pair.Second.Version, StringComparison.Ordinal));

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

            // A placeholder's version is invented, so its range is not judged
            // against it — but it is still a known requirement on that name,
            // and it may contradict one recorded elsewhere.
            var range = ParseRange(relation.Version, relation.Name);
            if (Uncertain(relation.Name))
            {
                var ranges = Constraints(relation.Name);
                ranges.Add(range);
                if (Contradicts(relation.Name))
                {
                    _lastConflict = Fail(
                        $"version ranges for {relation.Name} contradict each other "
                        + $"({string.Join(", ", ranges.Distinct())})");
                    return false;
                }

                continue;
            }

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
        List<SeiDependency>? conflicts,
        string label)
    {
        foreach (var conflict in conflicts ?? [])
        {
            var range = ParseRange(conflict.Version, conflict.Name);
            if (!_assigned.TryGetValue(conflict.Name, out var target))
            {
                continue;
            }

            if (Uncertain(conflict.Name))
            {
                // The placeholder may well collide with this; record what is
                // ruled out so a branch whose own ranges cover the whole
                // exclusion is dropped now instead of at its leaf.
                var excluded = _unknownExcluded.TryGetValue(conflict.Name, out var list)
                    ? list
                    : _unknownExcluded[conflict.Name] = [];
                excluded.Add(range);
                if (Contradicts(conflict.Name))
                {
                    _lastConflict = Fail(
                        $"{label}: {conflict.Name} cannot satisfy both its ranges and the conflict from {owner} {resolved.Version}");
                    return false;
                }

                continue;
            }

            if (range.Satisfies(target.ParsedVersion))
            {
                _lastConflict = Fail(
                    $"{label}: {owner} {resolved.Version} cannot coexist with {conflict.Name} {target.Version}");
                return false;
            }
        }

        return true;
    }

    private bool HintAllows(string name, NuGetVersion version)
        => _prereleaseHints.TryGetValue(name, out var ranges)
            && ranges.Any(range => range.Satisfies(version));

    internal static bool RangeAllowsPrerelease(VersionRange range)
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
            "resolve")
        {
            Hint = "relax the version range, or add a source that carries a satisfying version",
            HintKey = "hint.resolve",
        };
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

    // One budget for the whole operation: the main search and every probe
    // share it, so a wide set of alternatives cannot multiply the cap.
    private sealed class SearchBudget
    {
        public bool Exhausted { get; private set; }

        public bool DepthSpent { get; set; }

        public bool TryConsume()
        {
            if (Exhausted)
            {
                return false;
            }

            if (++_used <= MaxTotalAssignments)
            {
                return true;
            }

            Exhausted = true;
            return false;
        }

        private long _used;
    }

    private sealed record ForcedPick(string Version, ArtifactEntry Artifact);

    private sealed record Requirement(
        string Name,
        VersionRange Range,
        string? From);
}
