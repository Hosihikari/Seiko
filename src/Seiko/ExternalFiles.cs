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

        var paths = new HashSet<string>(StringComparer.Ordinal);
        // Deployment folds case and Unicode normalization and rejects
        // file/directory overlaps, so declarations must already be unique
        // under those rules (ADR-0024).
        var collisions = new PathCollisions();
        var fileIdentities = new Dictionary<string, ExternalFile>(StringComparer.Ordinal);
        var declaredBytes = 0L;
        foreach (var file in files)
        {
            ValidateTarget(file.Path, name, code);
            if (!paths.Add(file.Path))
            {
                throw new SeikoException($"{name}: duplicate external target path '{file.Path}'", code);
            }

            if (!collisions.TryAdd(file.Path, out var fileCollision))
            {
                throw Collision(name, file.Path, fileCollision!, code);
            }

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

            if (archive.Members.Count == 0)
            {
                throw new SeikoException($"{name}: external archive has no members", code);
            }

            // One member may feed several targets, so member names repeat;
            // only the target paths must stay unique.
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in archive.Members)
            {
                ValidateMemberName(member.Member, name, code);
                ValidateTarget(member.Path, name, code);
                declared.Add(member.Member);

                if (!paths.Add(member.Path))
                {
                    throw new SeikoException(
                        $"{name}: duplicate external target path '{member.Path}'",
                        code);
                }

                if (!collisions.TryAdd(member.Path, out var memberCollision))
                {
                    throw Collision(name, member.Path, memberCollision!, code);
                }
            }
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
    // and must agree field by field (ADR-0032, ADR-0037).
    public static ExternalSummary Summarize(ArtifactManifest? artifact)
        => new()
        {
            Files = artifact?.ExternalFiles?.Count ?? 0,
            Archives = artifact?.ExternalArchives?.Count ?? 0,
            Size = (artifact?.ExternalFiles ?? []).Sum(file => file.Size)
                + (artifact?.ExternalArchives ?? []).Sum(archive => archive.Size),
            Hosts = Hosts(artifact).ToList(),
        };

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
            .Select(item => Host(item.Url))
            .Concat((artifact.ExternalArchives ?? []).Select(item => Host(item.Url)))
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase);
    }

    public static int Count(ArtifactManifest? artifact)
        => artifact is null ? 0 : (artifact.ExternalFiles?.Count ?? 0) + (artifact.ExternalArchives?.Count ?? 0);

    private static SeikoException Collision(string name, string path, PathCollision collision, string code)
        => new(
            collision.Kind == PathCollisionKind.SamePath
                ? $"{name}: external target path '{path}' also exists as '{collision.Other}' by case or Unicode normalization"
                : $"{name}: path '{collision.Other}' is both a file and a directory of external target '{path}'",
            code);

    private static string Host(string url)
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
