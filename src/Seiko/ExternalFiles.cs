namespace Seiko;

// External payload: bytes that live at a vendor URL instead of inside the
// metadata zip (ADR-0032). The manifest digest is the authority; the URL is
// only a location, so a redirect or a replaced vendor file still fails here.
internal static class ExternalFiles
{
    public static readonly string[] Formats = ["zip", "tar", "tar.gz"];

    public static void Validate(ArtifactManifest artifact, string name, string code = "io")
    {
        var files = artifact.ExternalFiles ?? [];
        var archives = artifact.ExternalArchives ?? [];
        if (files.Count == 0 && archives.Count == 0)
        {
            return;
        }

        // Cheap pre-check before the per-target work: declarations alone may
        // already exceed the entry budget (ADR-0024). Nulls are tolerated here
        // and reported by the loops below.
        var declaredTargets = files.Count + archives.Sum(archive => archive?.Members?.Count ?? 0);
        if (declaredTargets > ArchiveLimits.MaxEntries)
        {
            throw new SeikoException(
                $"{name}: external declarations name {declaredTargets} targets; the limit is {ArchiveLimits.MaxEntries}",
                code);
        }

        // Deployment folds case and Unicode normalization and rejects
        // file/directory overlaps, so declarations must already be unique
        // under those rules (ADR-0024). Collisions are checked once at the end
        // so the per-target work stays linear.
        var collisions = new PathCollisions();
        var fileIdentities = new Dictionary<string, ExternalFile>(StringComparer.Ordinal);
        var declaredBytes = 0L;
        foreach (var file in files)
        {
            // JSON `null` in an array deserializes to a null element even
            // though the model list is non-nullable.
            if (file is null)
            {
                throw new SeikoException($"{name}: external file declaration is null", code);
            }

            ValidateTarget(file.Path, name, code);
            collisions.Add(file.Path);
            EnsureHttps(file.Url, $"external file '{file.Path}'", name, code);
            EnsureDigest(file.Sha256, $"external file '{file.Path}'", name, code);
            EnsureSize(file.Size, $"external file '{file.Path}'", name, code);

            // One URL is downloaded once (ADR-0032); repeated declarations may
            // fan out to more targets but must describe the same blob.
            if (fileIdentities.TryGetValue(file.Url, out var firstFile))
            {
                if (!string.Equals(firstFile.Sha256, file.Sha256, StringComparison.Ordinal)
                    || (firstFile.Size > 0 && file.Size > 0 && firstFile.Size != file.Size))
                {
                    throw new SeikoException(
                        $"{name}: repeated external file '{SafeUrl(file.Url)}' must keep the same digest and size",
                        code);
                }
            }
            else
            {
                fileIdentities[file.Url] = file;
                declaredBytes += file.Size;
            }
        }

        var archiveIdentities = new Dictionary<string, ExternalArchive>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            if (archive is null)
            {
                throw new SeikoException($"{name}: external archive declaration is null", code);
            }

            EnsureHttps(archive.Url, "external archive", name, code);
            EnsureDigest(archive.Sha256, "external archive", name, code);
            EnsureSize(archive.Size, "external archive", name, code);
            if (!Formats.Contains(archive.Format, StringComparer.Ordinal))
            {
                throw new SeikoException(
                    $"{name}: external archive format must be one of {string.Join(", ", Formats)}",
                    code);
            }

            // One URL pins one blob (ADR-0013). Repeated declarations may add
            // members, but they must describe the same download.
            if (archiveIdentities.TryGetValue(archive.Url, out var first))
            {
                if (!string.Equals(first.Sha256, archive.Sha256, StringComparison.Ordinal)
                    || !string.Equals(first.Format, archive.Format, StringComparison.Ordinal)
                    || (first.Size > 0 && archive.Size > 0 && first.Size != archive.Size))
                {
                    throw new SeikoException(
                        $"{name}: repeated external archive '{SafeUrl(archive.Url)}' must keep the same digest, format, and size",
                        code);
                }
            }
            else
            {
                archiveIdentities[archive.Url] = archive;
                declaredBytes += archive.Size;
            }

            if (archive.Members is null || archive.Members.Count == 0)
            {
                throw new SeikoException($"{name}: external archive has no members", code);
            }

            // One member may feed several targets, so member names repeat;
            // only the target paths must stay unique.
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in archive.Members)
            {
                if (member is null)
                {
                    throw new SeikoException($"{name}: external archive member is null", code);
                }

                ValidateMemberName(member.Member, name, code);
                ValidateTarget(member.Path, name, code);
                declared.Add(member.Member);
                collisions.Add(member.Path);
            }
        }

        if (collisions.TryFind(out var collision))
        {
            throw Collision(name, collision!, code);
        }

        // The same layered budget as artifact extraction (ADR-0024): many
        // individually valid declarations must not add up past 4 GiB.
        if (declaredBytes > ArchiveLimits.MaxTotalBytes)
        {
            throw new SeikoException(
                $"{name}: external payloads declare {declaredBytes} bytes; the limit is {ArchiveLimits.MaxTotalBytes}",
                code);
        }
    }

    // The derived summary of an artifact's declarations; the index mirrors it
    // and must agree field by field (ADR-0032, ADR-0037). A malformed element
    // is skipped rather than thrown on, because the verifier keeps going after
    // reporting the structural error (ADR-0020).
    public static ExternalSummary Summarize(ArtifactManifest? artifact)
        => new()
        {
            Files = artifact?.ExternalFiles?.Count ?? 0,
            Archives = artifact?.ExternalArchives?.Count ?? 0,
            // A single out-of-range size is reported by Validate, but the
            // preview and the sync warning reach this sum without validating
            // first, so it saturates instead of throwing (ADR-0029, ADR-0037).
            Size = SumSize(artifact),
            Hosts = Hosts(artifact).ToList(),
        };

    private static long SumSize(ArtifactManifest? artifact)
    {
        // LINQ's checked Sum overflows; the reported figure is an estimate.
        var total = 0L;
        foreach (var size in (artifact?.ExternalFiles ?? []).Select(file => file?.Size ?? 0)
            .Concat((artifact?.ExternalArchives ?? []).Select(archive => archive?.Size ?? 0)))
        {
            total = size > long.MaxValue - total ? long.MaxValue : total + size;
        }

        return total;
    }

    public static bool SummaryMatches(ExternalSummary declared, ArtifactManifest? artifact)
    {
        var expected = Summarize(artifact);
        if (declared.Files != expected.Files || declared.Archives != expected.Archives)
        {
            return false;
        }

        // Size stays optional metadata: a declaration may leave it at 0.
        if (declared.Size != 0 && declared.Size != expected.Size)
        {
            return false;
        }

        // Hosts are the disclosure that matters most; a wrong summary must not
        // pass just because the counts line up.
        var declaredHosts = (declared.Hosts ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return declaredHosts.SetEquals(expected.Hosts);
    }

    public static IEnumerable<string> Hosts(ArtifactManifest? artifact)
    {
        if (artifact is null)
        {
            return [];
        }

        return (artifact.ExternalFiles ?? [])
            .Select(item => Host(item?.Url))
            .Concat((artifact.ExternalArchives ?? []).Select(item => Host(item?.Url)))
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase);
    }

    public static int Count(ArtifactManifest? artifact)
        => artifact is null ? 0 : (artifact.ExternalFiles?.Count ?? 0) + (artifact.ExternalArchives?.Count ?? 0);

    private static SeikoException Collision(string name, PathCollision collision, string code)
        => new(
            collision.Kind == PathCollisionKind.SamePath
                ? $"{name}: external target path '{collision.Other}' also exists by case or Unicode normalization"
                : $"{name}: path '{collision.Other}' is both a file and a directory of another external target",
            code);

    private static string Host(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";

    // Errors are persisted or handed to MCP callers; a signed external URL
    // must not leak its userinfo, query, or fragment (ADR-0024).
    private static string SafeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Urls.Redact(uri) : url;

    private static void EnsureHttps(string url, string label, string name, string code)
    {
        // Same scheme rule as hosted artifact URLs (http or https), plus no
        // userinfo: the manifest is public and immutable, credentials there
        // would be visible to everyone.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new SeikoException($"{name}: {label} URL must be an absolute http(s) URL", code);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SeikoException($"{name}: {label} URL must not embed userinfo", code);
        }
    }

    private static void EnsureDigest(string sha256, string label, string name, string code)
    {
        if (!Hashing.IsSha256(sha256))
        {
            throw new SeikoException($"{name}: {label} needs a lowercase SHA-256 digest", code);
        }
    }

    private static void EnsureSize(long size, string label, string name, string code)
    {
        if (size < 0 || size > ArchiveLimits.MaxEntryBytes)
        {
            throw new SeikoException($"{name}: {label} size is out of range", code);
        }
    }

    private static void ValidateTarget(string path, string name, string code)
    {
        // An explicit JSON null arrives here as a null string, and both the
        // length bound and the relative-path check read it.
        if (path is null)
        {
            throw new SeikoException($"{name}: external target path is null", code);
        }

        // External targets go through the same ancestor lookups as archive
        // names, whose cost is quadratic in length, so they need the same
        // length and depth bounds (ADR-0024).
        if (ArchiveLimits.ExceedsEntryNameLimits(path))
        {
            throw new SeikoException(
                $"{name}: external target path is too long or too deep '{path}'",
                code);
        }

        if (!StoreDigests.IsSafeRelativePath(path))
        {
            throw new SeikoException($"{name}: unsafe external target path '{path}'", code);
        }

        if (path is "sei.json" or "tooth.json")
        {
            throw new SeikoException($"{name}: external target path '{path}' is reserved", code);
        }
    }

    // Archive member names may use backslashes or a leading "./"; both are
    // normalized here so validation and extraction agree.
    public static string NormalizeMember(string member)
    {
        var value = member.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    private static void ValidateMemberName(string member, string name, string code)
    {
        if (member is null)
        {
            throw new SeikoException($"{name}: external archive member name is null", code);
        }

        var normalized = NormalizeMember(member);
        if (normalized.Length == 0
            || normalized.EndsWith('/')
            || normalized.StartsWith('/')
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new SeikoException($"{name}: unsafe archive member '{member}'", code);
        }

        if (ArchiveLimits.ExceedsEntryNameLimits(normalized))
        {
            throw new SeikoException($"{name}: archive member path is too long or deep '{member}'", code);
        }
    }
}
