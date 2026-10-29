using NuGet.Versioning;

namespace Seiko;

public static class LockReplay
{
    public static IReadOnlyList<ResolvedSei> Resolve(
        IReadOnlyList<ISeiSource> sources,
        SeikoLock lockFile,
        IReadOnlyList<SeiSpec> specs)
    {
        foreach (var spec in specs)
        {
            var locked = lockFile.Sei.FirstOrDefault(p => string.Equals(p.Name, spec.Name, StringComparison.Ordinal));
            if (locked is null)
            {
                throw Fail($"--frozen: {spec.Name} is not in the lock");
            }

            var range = ParseRange(spec.Version, spec.Name);
            if (!NuGetVersion.TryParse(locked.Version, out var version) || !range.Satisfies(version))
            {
                throw Fail($"--frozen: locked {spec.Name} {locked.Version} does not satisfy {range}");
            }
        }

        var result = new List<ResolvedSei>();
        foreach (var locked in lockFile.Sei.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var source = sources.FirstOrDefault(s => s.GetSei(locked.Name) is not null)
                ?? throw Fail($"--frozen: source for {locked.Name} not found");

            var index = source.GetSei(locked.Name)!;
            var entry = index.Versions.FirstOrDefault(v => string.Equals(v.Version, locked.Version, StringComparison.Ordinal))
                ?? throw Fail($"--frozen: {locked.Name} {locked.Version} not found in source");

            var artifact = entry.Artifacts.FirstOrDefault(a =>
                    string.Equals(a.Source.Sha256, locked.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? throw Fail(
                    $"--frozen: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} not found in source");

            if (!NuGetVersion.TryParse(locked.Version, out var version))
            {
                throw Fail($"--frozen: invalid locked version '{locked.Version}' for {locked.Name}");
            }

            result.Add(new ResolvedSei
            {
                Name = locked.Name,
                ParsedVersion = version,
                Entry = entry,
                Source = source,
                Direct = locked.Direct,
                Artifact = artifact,
            });
        }

        return result;
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
