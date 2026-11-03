using NuGet.Versioning;

namespace Seiko;

public static class LockReplay
{
    public static IReadOnlyList<ResolvedSei> Resolve(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock lockFile,
        IReadOnlyList<SeiSpec> specs)
    {
        ValidateGraph(lockFile);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            declared.Add(spec.Name);
            var locked = lockFile.Sei.FirstOrDefault(p => string.Equals(p.Name, spec.Name, StringComparison.Ordinal));
            if (locked is null)
            {
                throw Fail($"--frozen: {spec.Name} is not in the lock");
            }

            if (!locked.Direct)
            {
                throw Fail($"--frozen: {spec.Name} is declared but locked as transitive");
            }

            var range = ParseRange(spec.Version, spec.Name);
            if (!NuGetVersion.TryParse(locked.Version, out var version) || !range.Satisfies(version))
            {
                throw Fail($"--frozen: locked {spec.Name} {locked.Version} does not satisfy {range}");
            }
        }

        foreach (var locked in lockFile.Sei.Where(p => p.Direct))
        {
            if (!declared.Contains(locked.Name))
            {
                throw Fail($"--frozen: {locked.Name} is direct in the lock but not declared");
            }
        }

        var result = new List<ResolvedSei>();
        foreach (var locked in lockFile.Sei.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var source = sources.FirstOrDefault(s => string.Equals(s.Id, locked.Source, StringComparison.Ordinal))
                ?? throw Fail(
                    $"--frozen: source '{locked.Source}' for {locked.Name} is not configured");

            var index = source.GetSei(locked.Name)
                ?? throw Fail($"--frozen: {locked.Name} is not in source '{locked.Source}'");
            SourceValidation.EnsureIndex(index, locked.Source, "lock");
            if (!NuGetVersion.TryParse(locked.Version, out var version))
            {
                throw Fail($"--frozen: invalid locked version '{locked.Version}' for {locked.Name}");
            }

            var matches = index.Versions
                .Where(entry => NuGetVersion.TryParse(entry.Version, out var entryVersion)
                    && entryVersion.Equals(version))
                .ToList();
            if (matches.Count == 0)
            {
                throw Fail($"--frozen: {locked.Name} {locked.Version} not found in source '{locked.Source}'");
            }

            if (matches.Count > 1)
            {
                throw Fail($"--frozen: {locked.Name} {locked.Version} is ambiguous in source '{locked.Source}'");
            }

            var entry = matches[0];

            var artifact = MatchArtifact(entry, locked)
                ?? throw Fail(
                    $"--frozen: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} is not in source '{locked.Source}'");

            if (!DependenciesEqual(entry.Dependencies, locked.Dependencies)
                || !DependenciesEqual(entry.Prerequisites, locked.Prerequisites)
                || !DependenciesEqual(entry.OptionalPeers, locked.OptionalPeers)
                || !DependenciesEqual(entry.Conflicts, locked.Conflicts))
            {
                throw Fail($"--frozen: relations of {locked.Name} {locked.Version} changed in source '{locked.Source}'");
            }

            if (!string.Equals(artifact.Rid, locked.Artifact.Rid, StringComparison.OrdinalIgnoreCase)
                || !DependenciesEqual(artifact.Dependencies, locked.Artifact.Dependencies)
                || !DependenciesEqual(artifact.Prerequisites, locked.Artifact.Prerequisites)
                || !DependenciesEqual(artifact.OptionalPeers, locked.Artifact.OptionalPeers)
                || !DependenciesEqual(artifact.Conflicts, locked.Artifact.Conflicts))
            {
                throw Fail($"--frozen: artifact metadata of {locked.Name} {locked.Version} changed in source '{locked.Source}'");
            }

            result.Add(new ResolvedSei
            {
                Name = locked.Name,
                ParsedVersion = version,
                Entry = entry,
                Source = source,
                Direct = locked.Direct,
                Artifact = artifact,
                PackageDeprecated = index.Deprecated,
            });
        }

        CrossSourceCheck.Verify(sources, result, "lock");
        return result;
    }

    private static ArtifactEntry? MatchArtifact(SeiVersionEntry entry, LockedSei locked)
    {
        var candidates = entry.Artifacts
            .Where(artifact =>
                string.Equals(artifact.Source.Sha256, locked.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(artifact.Rid, locked.Artifact.Rid, StringComparison.OrdinalIgnoreCase)
                && DependenciesEqual(artifact.Dependencies, locked.Artifact.Dependencies)
                && DependenciesEqual(artifact.Prerequisites, locked.Artifact.Prerequisites)
                && DependenciesEqual(artifact.OptionalPeers, locked.Artifact.OptionalPeers)
                && DependenciesEqual(artifact.Conflicts, locked.Artifact.Conflicts))
            .ToList();

        if (candidates.Count > 1)
        {
            throw Fail(
                $"--frozen: artifact for {locked.Name} {locked.Version} is ambiguous in source '{locked.Source}'");
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static void ValidateGraph(SeikoLock lockFile)
    {
        // A lock is only valid for the platform it was produced on (ADR-0025).
        foreach (var locked in lockFile.Sei)
        {
            if (!string.IsNullOrWhiteSpace(locked.Artifact.Rid)
                && !locked.Artifact.Rid.Equals(lockFile.Target.Rid, StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(
                    $"--frozen: artifact of {locked.Name} {locked.Version} targets rid '{locked.Artifact.Rid}', but the lock targets '{lockFile.Target.Rid}'");
            }
        }

        var byName = new Dictionary<string, LockedSei>(StringComparer.Ordinal);
        foreach (var locked in lockFile.Sei)
        {
            if (!byName.TryAdd(locked.Name, locked))
            {
                throw Fail($"--frozen: duplicate lock entry for {locked.Name}");
            }
        }

        foreach (var locked in lockFile.Sei)
        {
            foreach (var dependency in locked.Dependencies)
            {
                if (string.Equals(dependency.Name, locked.Name, StringComparison.Ordinal))
                {
                    throw Fail($"--frozen: {locked.Name} depends on itself");
                }

                if (!byName.TryGetValue(dependency.Name, out var dependencyLocked))
                {
                    throw Fail(
                        $"--frozen: {locked.Name} requires {dependency.Name}, which is missing from the lock");
                }

                var range = ParseRange(dependency.Version, dependency.Name);
                if (!NuGetVersion.TryParse(dependencyLocked.Version, out var dependencyVersion)
                    || !range.Satisfies(dependencyVersion))
                {
                    throw Fail(
                        $"--frozen: {locked.Name} requires {dependency.Name} {dependency.Version}, but the lock has {dependencyLocked.Version}");
                }
            }

            ValidateRelations(byName, locked, locked.Prerequisites, "prerequisite", requirePresent: true);
            ValidateRelations(byName, locked, locked.OptionalPeers, "optional peer", requirePresent: false);
            ValidateConflicts(byName, locked, locked.Conflicts);
            ValidateRelations(byName, locked, locked.Artifact.Dependencies, "artifact dependency", requirePresent: true);
            ValidateRelations(byName, locked, locked.Artifact.Prerequisites, "artifact prerequisite", requirePresent: true);
            ValidateRelations(byName, locked, locked.Artifact.OptionalPeers, "artifact optional peer", requirePresent: false);
            ValidateConflicts(byName, locked, locked.Artifact.Conflicts);
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(
            lockFile.Sei.Where(entry => entry.Direct).Select(entry => entry.Name));
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!reachable.Add(name))
            {
                continue;
            }

            foreach (var dependency in byName[name].Dependencies
                .Concat(byName[name].Artifact.Dependencies))
            {
                pending.Push(dependency.Name);
            }
        }

        foreach (var locked in lockFile.Sei)
        {
            if (!reachable.Contains(locked.Name))
            {
                throw Fail(
                    $"--frozen: {locked.Name} is not reachable from the direct declarations");
            }
        }
    }

    private static void ValidateRelations(
        Dictionary<string, LockedSei> byName,
        LockedSei owner,
        List<SeiDependency>? relations,
        string kind,
        bool requirePresent)
    {
        foreach (var relation in relations ?? [])
        {
            if (string.Equals(relation.Name, owner.Name, StringComparison.Ordinal))
            {
                throw Fail($"--frozen: {owner.Name} has a self {kind}");
            }

            if (!byName.TryGetValue(relation.Name, out var target))
            {
                if (!requirePresent)
                {
                    continue;
                }

                throw Fail(
                    $"--frozen: {owner.Name} {owner.Version} has {kind} {relation.Name}, which is missing from the lock");
            }

            var range = ParseRange(relation.Version, relation.Name);
            if (!NuGetVersion.TryParse(target.Version, out var targetVersion)
                || !range.Satisfies(targetVersion))
            {
                throw Fail(
                    $"--frozen: {owner.Name} {owner.Version} requires {kind} {relation.Name} {relation.Version}, but the lock has {target.Version}");
            }
        }
    }

    private static void ValidateConflicts(
        Dictionary<string, LockedSei> byName,
        LockedSei owner,
        List<SeiDependency>? conflicts)
    {
        foreach (var conflict in conflicts ?? [])
        {
            if (!byName.TryGetValue(conflict.Name, out var target))
            {
                continue;
            }

            if (NuGetVersion.TryParse(target.Version, out var targetVersion)
                && ParseRange(conflict.Version, conflict.Name).Satisfies(targetVersion))
            {
                throw Fail(
                    $"--frozen: {owner.Name} {owner.Version} conflicts with {conflict.Name} {target.Version}");
            }
        }
    }

    public static string? Compare(SeikoLock existing, SeikoLock resolved)
    {
        if (!string.Equals(existing.Target.Rid, resolved.Target.Rid, StringComparison.Ordinal))
        {
            return $"rid {existing.Target.Rid} -> {resolved.Target.Rid}";
        }

        if (!existing.Sources.SequenceEqual(resolved.Sources, StringComparer.Ordinal))
        {
            return "sources changed";
        }

        var left = existing.Sei.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var right = resolved.Sei.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (left.Count != right.Count)
        {
            return $"sei count {left.Count} -> {right.Count}";
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !string.Equals(a.Version, b.Version, StringComparison.Ordinal)
                || !string.Equals(a.Source, b.Source, StringComparison.Ordinal)
                || a.Direct != b.Direct
                || !string.Equals(a.Artifact.Sha256, b.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.Artifact.Rid, b.Artifact.Rid, StringComparison.OrdinalIgnoreCase)
                || !DependenciesEqual(a.Dependencies, b.Dependencies)
                || !DependenciesEqual(a.Prerequisites, b.Prerequisites)
                || !DependenciesEqual(a.OptionalPeers, b.OptionalPeers)
                || !DependenciesEqual(a.Conflicts, b.Conflicts)
                || !DependenciesEqual(a.Artifact.Dependencies, b.Artifact.Dependencies)
                || !DependenciesEqual(a.Artifact.Prerequisites, b.Artifact.Prerequisites)
                || !DependenciesEqual(a.Artifact.OptionalPeers, b.Artifact.OptionalPeers)
                || !DependenciesEqual(a.Artifact.Conflicts, b.Artifact.Conflicts))
            {
                return $"{a.Name} differs";
            }
        }

        return null;
    }

    private static bool DependenciesEqual(List<SeiDependency>? left, List<SeiDependency>? right)
    {
        var a = (left ?? []).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var b = (right ?? []).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        return a.Count == b.Count
            && a.Zip(b).All(pair =>
                string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && string.Equals(pair.First.Version ?? "", pair.Second.Version ?? "", StringComparison.Ordinal));
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

    private static SeikoException Fail(string message) => new(message, "lock");
}
