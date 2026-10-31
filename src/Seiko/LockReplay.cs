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

            var artifact = entry.Artifacts.FirstOrDefault(a =>
                    string.Equals(a.Source.Sha256, locked.Artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                    && SelectorsEqual(a.Selectors, locked.Artifact.Selectors))
                ?? throw Fail(
                    $"--frozen: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} is not in source '{locked.Source}'");

            if (!DependenciesEqual(entry.Dependencies, locked.Dependencies))
            {
                throw Fail($"--frozen: dependencies of {locked.Name} {locked.Version} changed in source '{locked.Source}'");
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

        return result;
    }

    private static void ValidateGraph(SeikoLock lockFile)
    {
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

            foreach (var dependency in byName[name].Dependencies)
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
                || !SelectorsEqual(a.Artifact.Selectors, b.Artifact.Selectors)
                || !DependenciesEqual(a.Dependencies, b.Dependencies))
            {
                return $"{a.Name} differs";
            }
        }

        return null;
    }

    private static bool SelectorsEqual(Dictionary<string, string>? left, Dictionary<string, string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        return a.Count == b.Count
            && a.All(pair => b.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool DependenciesEqual(List<SeiDependency> left, List<SeiDependency> right)
    {
        var a = left.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var b = right.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
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
