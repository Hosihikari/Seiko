using NuGet.Versioning;

namespace Seiko.Core;

public static class LockReplay
{
    public static IReadOnlyList<ResolvedPackage> Resolve(
        IReadOnlyList<LocalSource> sources,
        SeikoLock lockFile,
        IReadOnlyList<PackageSpec> specs)
    {
        foreach (var spec in specs)
        {
            var locked = lockFile.Packages.FirstOrDefault(p => string.Equals(p.Name, spec.Name, StringComparison.Ordinal));
            if (locked is null)
            {
                throw new SeikoException($"--frozen: {spec.Name} is not in the lock");
            }

            var range = ParseRange(spec.Version, spec.Name);
            if (!NuGetVersion.TryParse(locked.Version, out var version) || !range.Satisfies(version))
            {
                throw new SeikoException($"--frozen: locked {spec.Name} {locked.Version} does not satisfy {range}");
            }
        }

        var result = new List<ResolvedPackage>();
        foreach (var locked in lockFile.Packages.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var source = sources.FirstOrDefault(s => s.GetPackage(locked.Name) is not null)
                ?? throw new SeikoException($"--frozen: source for {locked.Name} not found");

            var index = source.GetPackage(locked.Name)!;
            var entry = index.Versions.FirstOrDefault(v => string.Equals(v.Version, locked.Version, StringComparison.Ordinal))
                ?? throw new SeikoException($"--frozen: {locked.Name} {locked.Version} not found in source");

            var artifact = entry.Artifacts.FirstOrDefault(a =>
                    string.Equals(a.Source.Sha256, locked.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? throw new SeikoException(
                    $"--frozen: artifact {locked.Artifact.Sha256} for {locked.Name} {locked.Version} not found in source");

            if (!NuGetVersion.TryParse(locked.Version, out var version))
            {
                throw new SeikoException($"--frozen: invalid locked version '{locked.Version}' for {locked.Name}");
            }

            result.Add(new ResolvedPackage
            {
                Name = locked.Name,
                ParsedVersion = version,
                Entry = entry,
                Source = source,
                Direct = locked.Direct,
                Artifact = artifact,
                ArtifactPath = source.ResolveArtifactPath(artifact.Source),
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

        var left = existing.Packages.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var right = resolved.Packages.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (left.Count != right.Count)
        {
            return $"package count {left.Count} -> {right.Count}";
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

    private static VersionRange ParseRange(string? text, string package)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*")
        {
            return VersionRange.All;
        }

        if (!VersionRange.TryParse(text, out var range))
        {
            throw new SeikoException($"invalid version range '{text}' for {package}");
        }

        return range;
    }
}
