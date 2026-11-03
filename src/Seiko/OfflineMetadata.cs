using NuGet.Versioning;

namespace Seiko;

// Offline mode may only use cached metadata and stored artifacts. Missing
// pieces are collected up front so the user sees one list instead of the
// first failure, and nothing is downloaded implicitly.
internal static class OfflineMetadata
{
    public static void Verify(
        SeikoConfig config,
        string storePath,
        IEnumerable<string> initial,
        bool followIndexLists = false)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var httpSources = config.Sources
            .Where(IsHttp)
            .Select(source => (Source: source, IndexUri: new Uri(source.Index)))
            .ToList();
        var pending = new Queue<string>(initial);

        foreach (var (source, indexUri) in httpSources)
        {
            var path = MetadataCache.PathFor(storePath, indexUri.AbsoluteUri);
            if (!File.Exists(path))
            {
                missing.Add($"index metadata of source '{source.Id}'");
                continue;
            }

            if (!followIndexLists)
            {
                continue;
            }

            // search walks the root list, so every listed name needs an entry.
            var root = MetadataCache.TryRead(path, SeikoJsonContext.Default.IndexRoot);
            if (root is null)
            {
                missing.Add($"index metadata of source '{source.Id}' (unreadable)");
                continue;
            }

            foreach (var name in root.Sei ?? [])
            {
                pending.Enqueue(name);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!SeiName.IsValid(name) || !seen.Add(name))
            {
                continue;
            }

            SeiIndex? found = null;
            foreach (var source in config.Sources)
            {
                if (IsHttp(source))
                {
                    var uri = new Uri(new Uri(source.Index), $"sei/{name}.json");
                    var path = MetadataCache.PathFor(storePath, uri.AbsoluteUri);
                    if (!File.Exists(path))
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}'");
                        continue;
                    }

                    found = MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex);
                    if (found is null)
                    {
                        missing.Add($"sei/{name}.json from source '{source.Id}' (unreadable)");
                    }

                    break;
                }

                found = LocalIndex(source, name);
                break;
            }

            if (found is not null)
            {
                Enqueue(found, pending);
            }
        }

        if (missing.Count > 0)
        {
            throw new SeikoException(
                $"offline: missing cached metadata: {string.Join("; ", missing)}. "
                + "Run the command once online, then retry offline",
                "source");
        }
    }

    // Local sources are always readable; their files still contribute relation
    // names, and a missing local file is reported by the source itself later.
    private static SeiIndex? LocalIndex(SourceConfig source, string name)
    {
        if (source.Index.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var baseDirectory = Path.GetDirectoryName(
            Path.GetFullPath(source.Index.StartsWith('/') ? source.Index : source.Index));
        var path = Path.Combine(
            baseDirectory ?? "",
            "sei",
            name + ".json");
        return File.Exists(path)
            ? MetadataCache.TryRead(path, SeikoJsonContext.Default.SeiIndex)
            : null;
    }
    public static void EnsureArtifacts(Store store, IReadOnlyList<ResolvedSei> resolved)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sei in resolved.Where(sei => IsHttpUri(sei.Source)))
        {
            try
            {
                var stored = store.TryGet(sei.Artifact.Source.Sha256);
                if (stored is null
                    || !string.Equals(stored.Manifest.Name, sei.Name, StringComparison.Ordinal)
                    || !NuGetVersion.TryParse(stored.Manifest.Version, out var version)
                    || !version.Equals(sei.ParsedVersion))
                {
                    missing.Add($"{sei.Name} {sei.Version}");
                }
            }
            catch (SeikoException ex) when (ex.Code == "cache-repair")
            {
                missing.Add($"{sei.Name} {sei.Version}");
            }
        }

        if (missing.Count > 0)
        {
            throw new SeikoException(
                $"offline: missing cached artifacts: {string.Join(", ", missing)}. "
                + "Run `sk sync` while online to download them",
                "source");
        }
    }

    private static void Enqueue(SeiIndex index, Queue<string> pending)
    {
        foreach (var entry in index.Versions ?? [])
        {
            foreach (var relation in entry.Dependencies
                .Concat(entry.Prerequisites)
                .Concat(entry.OptionalPeers)
                .Concat(entry.Conflicts))
            {
                pending.Enqueue(relation.Name);
            }

            foreach (var artifact in entry.Artifacts ?? [])
            {
                foreach (var relation in (artifact?.Dependencies ?? [])
                    .Concat(artifact!.Prerequisites ?? [])
                    .Concat(artifact.OptionalPeers ?? [])
                    .Concat(artifact.Conflicts ?? []))
                {
                    pending.Enqueue(relation.Name);
                }
            }
        }
    }

    private static bool IsHttp(SourceConfig source) => IsHttpUri(source.Index);

    private static bool IsHttpUri(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUri(ISeiSource source) => source is HttpSource;
}
