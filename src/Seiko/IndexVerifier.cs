using System.IO.Compression;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;

namespace Seiko;

public sealed record IndexFinding(string Severity, string Code, string Message, string? Path);

public sealed record IndexVerifyResult(
    string Root,
    int Sei,
    int Versions,
    int Artifacts,
    int CheckedArtifacts,
    IReadOnlyList<IndexFinding> Findings)
{
    public int Errors => Findings.Count(finding => finding.Severity == "error");
    public int Warnings => Findings.Count(finding => finding.Severity == "warning");
}

public static class IndexVerifier
{
    // A maintainer validates one sei-index.json before submitting it; a repo
    // owner validates the whole index directory. Same rules, same result
    // shape, so local and CI feedback cannot drift apart.
    public static IndexVerifyResult VerifyPath(
        string path,
        bool checkArtifacts = false,
        string? proxy = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full)
            ? VerifyPackageFile(full, checkArtifacts, proxy, log, cancellationToken)
            : Verify(full, checkArtifacts, proxy, log, cancellationToken);
    }

    private static IndexVerifyResult VerifyPackageFile(
        string path,
        bool checkArtifacts,
        string? proxy,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var context = new Context(directory);

        SeiIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.SeiIndex);
        }
        catch (JsonException ex)
        {
            context.Error("sei-invalid", $"invalid {fileName}: {ex.Message}", fileName);
            return context.ToResult(0);
        }

        if (index is null)
        {
            context.Error("sei-invalid", $"{fileName} is empty", fileName);
            return context.ToResult(0);
        }

        if (!string.Equals(fileName, "sei-index.json", StringComparison.OrdinalIgnoreCase))
        {
            context.Warning(
                "sei-filename",
                $"file name is '{fileName}'; the registered name must be sei-index.json",
                fileName);
        }

        if (index.Upstream is not null || index.Maintainers is not null
            || index.SourceUnlisted is not null || index.SourceDeprecated is not null)
        {
            context.Error(
                "sei-side-field",
                "a maintainer file must not declare upstream, maintainers, or source* fields",
                fileName);
        }

        ValidateSeiBody(context, fileName, Path.GetFileNameWithoutExtension(fileName), index);
        if (index.Versions is { } entries)
        {
            foreach (var entry in entries)
            {
                if (entry?.SourceYanked is not null || entry?.SourceAdvisories is not null)
                {
                    context.Error(
                        "sei-side-field",
                        $"version '{entry!.Version}' must not declare source* fields",
                        fileName);
                }
            }
        }

        var checkedCount = 0;
        if (checkArtifacts && context.Artifacts.Count > 0)
        {
            checkedCount = CheckArtifacts(context, proxy, log, cancellationToken);
        }

        return context.ToResult(checkedCount);
    }

    public static IndexVerifyResult Verify(
        string directory,
        bool checkArtifacts = false,
        string? proxy = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool checkCatalog = true)
    {
        var root = Path.GetFullPath(directory);
        var context = new Context(root);

        if (!Directory.Exists(root))
        {
            context.Error("index-missing", $"index directory not found: {root}");
            return context.ToResult(0);
        }

        var indexPath = Path.Combine(root, "index.json");
        IndexRoot? index = null;
        if (!File.Exists(indexPath))
        {
            context.Error("index-missing", "index.json not found", "index.json");
        }
        else
        {
            index = ReadIndex(context, indexPath);
        }

        var listed = index?.Sei is null ? null : new HashSet<string>(StringComparer.Ordinal);
        if (index?.Sei is not null)
        {
            foreach (var name in index.Sei)
            {
                if (!SeiName.IsValid(name))
                {
                    context.Error("sei-name", $"invalid sei name '{name}'", "index.json");
                    continue;
                }

                if (!listed!.Add(name))
                {
                    context.Warning("index-duplicate", $"duplicate sei name '{name}' in index.json", "index.json");
                }
            }
        }
        else if (index is not null)
        {
            context.Warning(
                "index-list-missing",
                "index.json has no sei list; search will not see this index",
                "index.json");
        }

        var files = EnumerateSeiFiles(root);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!SeiName.IsValid(name))
            {
                context.Error("sei-name", $"invalid sei file name '{name}'", Relative(root, file));
                continue;
            }

            seen.Add(name);
            if (listed is not null && !listed.Contains(name))
            {
                context.Error(
                    "sei-not-listed",
                    $"sei '{name}' is not listed in index.json",
                    Relative(root, file));
            }

            ValidateSeiFile(context, file, name);
        }

        if (listed is not null)
        {
            foreach (var name in listed)
            {
                if (!seen.Contains(name))
                {
                    context.Error(
                        "sei-missing",
                        $"sei '{name}' is listed in index.json but sei/{name}.json is missing",
                        $"sei/{name}.json");
                }
            }
        }

        var checkedCount = 0;
        if (checkArtifacts && context.Artifacts.Count > 0)
        {
            checkedCount = CheckArtifacts(context, proxy, log, cancellationToken);
        }

        if (checkCatalog)
        {
            CheckCatalog(context, root);
        }

        return context.ToResult(checkedCount);
    }

    // The catalog is derived; a missing one is a warning unless --strict.
    // A present one must match what the mirrors say, or a hand edit can make
    // search disagree with installation.
    private static void CheckCatalog(Context context, string root)
    {
        var path = Path.Combine(root, CatalogFile.FileName);
        var existing = CatalogFile.TryRead(path);
        if (existing is null)
        {
            context.Warning(
                "catalog-missing",
                "catalog.json is missing or unreadable; search will fall back to per-name reads",
                CatalogFile.FileName);
            return;
        }

        // A mirror with a null version list (or no name) cannot produce a
        // trustworthy catalog. The structural findings above already describe
        // the problem; comparing against a derived catalog would only crash or
        // add noise.
        var mirrors = LoadAllSei(root).ToList();
        if (mirrors.Any(sei => sei.Versions is null || string.IsNullOrWhiteSpace(sei.Name)))
        {
            return;
        }

        var expected = CatalogFile.Build(mirrors);
        if (Serialize(existing) != Serialize(expected))
        {
            context.Error(
                "catalog-stale",
                "catalog.json does not match the index; regenerate it",
                CatalogFile.FileName);
        }
    }

    private static string Serialize(SeiCatalog catalog)
        => JsonSerializer.Serialize(catalog, SeikoJsonContext.Default.SeiCatalog);

    private static IEnumerable<SeiIndex> LoadAllSei(string root)
        => CatalogFile.Load(root);

    private static IndexRoot? ReadIndex(Context context, string path)
    {
        try
        {
            var index = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.IndexRoot);
            if (index is null)
            {
                context.Error("index-invalid", "index.json is empty", "index.json");
                return null;
            }

            if (index.SchemaVersion != IndexRoot.CurrentSchemaVersion)
            {
                context.Error(
                    "index-schema",
                    $"unsupported index schemaVersion {index.SchemaVersion}",
                    "index.json");
                return null;
            }

            return index;
        }
        catch (JsonException ex)
        {
            context.Error("index-invalid", $"invalid index.json: {ex.Message}", "index.json");
            return null;
        }
    }

    private static List<string> EnumerateSeiFiles(string root)
    {
        var directory = Path.Combine(root, "sei");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static void ValidateSeiFile(Context context, string path, string name)
    {
        var relative = Relative(context.Root, path);
        SeiIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(File.ReadAllBytes(path), SeikoJsonContext.Default.SeiIndex);
        }
        catch (JsonException ex)
        {
            context.Error("sei-invalid", $"invalid sei index: {ex.Message}", relative);
            return;
        }

        if (index is null)
        {
            context.Error("sei-invalid", "sei index is empty", relative);
            return;
        }

        if (index.SchemaVersion != SeiIndex.CurrentSchemaVersion)
        {
            context.Error(
                "sei-schema",
                $"unsupported sei schemaVersion {index.SchemaVersion}",
                relative);
            return;
        }

        if (!string.Equals(index.Name, name, StringComparison.Ordinal))
        {
            context.Error(
                "sei-name",
                $"sei file declares name '{index.Name}'",
                relative);
            return;
        }

        ValidateSeiBody(context, relative, name, index);
    }

    private static void ValidateSeiBody(
        Context context,
        string relative,
        string name,
        SeiIndex index)
    {
        context.Sei++;
        if (index.Deprecated is not null && string.IsNullOrWhiteSpace(index.Deprecated))
        {
            context.Warning(
                "deprecated-empty",
                $"sei '{name}' has an empty deprecated message",
                relative);
        }

        ValidateProject(context, relative, name, index.Project);
        ValidateUpstream(context, relative, name, index.Upstream, index.Maintainers);
        if (index.SourceUnlisted == false)
        {
            context.Warning(
                "source-overlay",
                $"sei '{name}' records sourceUnlisted false; it only ever ORs with unlisted",
                relative);
        }

        var versions = index.Versions ?? [];
        if (versions.Count == 0)
        {
            context.Warning("sei-empty", $"sei '{name}' has no versions", relative);
        }

        var seenVersions = new HashSet<NuGetVersion>();
        foreach (var entry in versions)
        {
            if (entry is null)
            {
                context.Error("version-invalid", "version entry is null", relative);
                continue;
            }

            var version = ValidateVersion(context, relative, name, entry, seenVersions);
            if (version is null)
            {
                continue;
            }

            context.Versions++;

            if (entry.Dependencies is null)
            {
                context.Error(
                    "dependency-list",
                    $"sei '{name}' {entry.Version} has a null dependency list",
                    relative);
            }

            var dependencies = (entry.Dependencies ?? [])
                .Where(dependency => dependency is not null)
                .ToList();
            var artifacts = entry.Artifacts ?? [];
            if (artifacts.Count == 0)
            {
                context.Error(
                    "artifact-empty",
                    $"sei '{name}' {entry.Version} has no artifacts",
                    relative);
                continue;
            }

            var conditions = new List<ArtifactEntry>();
            foreach (var artifact in artifacts)
            {
                if (artifact is null)
                {
                    context.Error(
                        "artifact-invalid",
                        $"sei '{name}' {entry.Version} has a null artifact",
                        relative);
                    continue;
                }

                context.ArtifactsCount++;
                if (!ValidateArtifact(context, relative, name, entry, artifact))
                {
                    continue;
                }

                if (conditions.Any(existing => ArtifactConditions.Equivalent(existing, artifact)))
                {
                    context.Error(
                        "artifact-ambiguous",
                        $"sei '{name}' {entry.Version} has artifacts with equivalent resolution conditions",
                        relative);
                    continue;
                }

                conditions.Add(artifact);
                context.Artifacts.Add(new ArtifactRef(
                    name,
                    entry.Version,
                    version,
                    dependencies,
                    entry.Prerequisites ?? [],
                    entry.OptionalPeers ?? [],
                    entry.Conflicts ?? [],
                    artifact,
                    relative));
            }
        }
    }

    private static NuGetVersion? ValidateVersion(
        Context context,
        string path,
        string name,
        SeiVersionEntry entry,
        HashSet<NuGetVersion> seenVersions)
    {
        if (!NuGetVersion.TryParse(entry.Version, out var version))
        {
            context.Error(
                "version-invalid",
                $"sei '{name}' has invalid version '{entry.Version}'",
                path);
            return null;
        }

        if (!seenVersions.Add(version))
        {
            context.Error(
                "version-duplicate",
                $"sei '{name}' lists version '{entry.Version}' more than once",
                path);
            return null;
        }

        foreach (var dependency in entry.Dependencies ?? [])
        {
            if (dependency is null || !SeiName.IsValid(dependency.Name))
            {
                context.Error(
                    "dependency-name",
                    $"sei '{name}' {entry.Version} has invalid dependency name '{dependency?.Name}'",
                    path);
                continue;
            }

            if (string.Equals(dependency.Name, name, StringComparison.Ordinal))
            {
                context.Error(
                    "dependency-self",
                    $"sei '{name}' {entry.Version} depends on itself",
                    path);
            }

            if (!IsValidRange(dependency.Version))
            {
                context.Error(
                    "dependency-range",
                    $"sei '{name}' {entry.Version} has invalid version range '{dependency.Version}' for '{dependency.Name}'",
                    path);
            }
        }

        ValidateRelations(context, path, name, entry.Version, "prerequisite", entry.Prerequisites);
        ValidateRelations(context, path, name, entry.Version, "optional peer", entry.OptionalPeers);
        ValidateRelations(context, path, name, entry.Version, "conflict", entry.Conflicts);

        if (entry.Deprecated is not null && string.IsNullOrWhiteSpace(entry.Deprecated))
        {
            context.Warning(
                "deprecated-empty",
                $"sei '{name}' {entry.Version} has an empty deprecated message",
                path);
        }

        if (entry.Advisory is { } advisory)
        {
            var severity = (advisory.Severity ?? "").Trim().ToLowerInvariant();
            if (severity is not ("low" or "moderate" or "high" or "critical"))
            {
                context.Error(
                    "advisory-severity",
                    $"sei '{name}' {entry.Version} has invalid advisory severity '{advisory.Severity}'",
                    path);
            }

            if (advisory.Url is not null && !Urls.TryGetHttp(advisory.Url, out _))
            {
                context.Error(
                    "advisory-url",
                    $"sei '{name}' {entry.Version} has invalid advisory URL '{advisory.Url}'",
                    path);
            }
        }

        return version;
    }

    // Project metadata is display-only; keep the requirements shallow but
    // enforced, because search and info read these fields.
    private static void ValidateProject(
        Context context,
        string path,
        string name,
        ProjectMetadata? project)
    {
        if (project is null)
        {
            context.Warning(
                "project-missing",
                $"sei '{name}' has no project metadata",
                path);
            return;
        }

        if (string.IsNullOrWhiteSpace(project.Description))
        {
            context.Error("project-field", $"sei '{name}' has no project description", path);
        }

        if (project.Authors is null || project.Authors.Count == 0
            || project.Authors.Any(author => string.IsNullOrWhiteSpace(author)))
        {
            context.Error("project-field", $"sei '{name}' has an empty project author list", path);
        }

        if (!SpdxExpression.IsValid(project.License))
        {
            context.Error(
                "project-license",
                $"sei '{name}' has an invalid SPDX license expression '{project.License}'",
                path);
        }

        foreach (var tag in project.Tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || !string.Equals(tag, tag.Trim(), StringComparison.Ordinal))
            {
                context.Error("project-tag", $"sei '{name}' has an invalid tag '{tag}'", path);
            }
        }

        foreach (var (field, url) in new[]
        {
            ("projectUrl", project.ProjectUrl),
            ("repositoryUrl", project.RepositoryUrl),
        })
        {
            if (url is not null && !IsHttps(url))
            {
                context.Error("project-url", $"sei '{name}' {field} must be an absolute https URL", path);
            }
        }

        if (project.RepositoryUrl is not null && string.IsNullOrWhiteSpace(project.RepositoryType))
        {
            context.Warning(
                "project-field",
                $"sei '{name}' has a repositoryUrl without repositoryType",
                path);
        }
    }

    private static void ValidateUpstream(
        Context context,
        string path,
        string name,
        UpstreamInfo? upstream,
        List<string>? maintainers)
    {
        if (maintainers is not null)
        {
            if (maintainers.Count == 0 || maintainers.Any(item => string.IsNullOrWhiteSpace(item)))
            {
                context.Error("maintainer", $"sei '{name}' has an empty maintainer entry", path);
            }

            if (maintainers.Count > 0 && !maintainers.Any(item => item.StartsWith("github:", StringComparison.Ordinal)))
            {
                context.Warning(
                    "maintainer",
                    $"sei '{name}' has no GitHub maintainer",
                    path);
            }
        }

        if (upstream is null)
        {
            // A freshly registered entry has no upstream yet; the fields are
            // still written by the mirror so the shape never changes.
            return;
        }

        if (string.IsNullOrEmpty(upstream.Url) && string.IsNullOrEmpty(upstream.Sha256))
        {
            return;
        }

        if (!IsHttps(upstream.Url) || !new Uri(upstream.Url).AbsolutePath.EndsWith("/sei-index.json", StringComparison.Ordinal))
        {
            context.Error(
                "upstream",
                $"sei '{name}' upstream must be an https URL ending in /sei-index.json",
                path);
        }

        if (!Hashing.IsSha256(upstream.Sha256))
        {
            context.Error("upstream", $"sei '{name}' upstream has no file digest", path);
        }
    }

    private static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);

    private static void ValidateRelations(
        Context context,
        string path,
        string name,
        string version,
        string kind,
        List<SeiDependency>? relations)
    {
        var code = kind.Replace(' ', '-');
        if (relations is null)
        {
            context.Error(
                $"{code}-list",
                $"sei '{name}' {version} has a null {kind} list",
                path);
            return;
        }

        foreach (var relation in relations)
        {
            if (relation is null || !SeiName.IsValid(relation.Name))
            {
                context.Error(
                    $"{code}-name",
                    $"sei '{name}' {version} has invalid {kind} name '{relation?.Name}'",
                    path);
                continue;
            }

            if (string.Equals(relation.Name, name, StringComparison.Ordinal))
            {
                context.Error(
                    $"{code}-self",
                    $"sei '{name}' {version} has a self {kind}",
                    path);
            }

            if (!IsValidRange(relation.Version))
            {
                context.Error(
                    $"{code}-range",
                    $"sei '{name}' {version} has invalid version range '{relation.Version}' for {kind} '{relation.Name}'",
                    path);
            }
        }
    }

    private static bool ValidateArtifact(
        Context context,
        string path,
        string name,
        SeiVersionEntry entry,
        ArtifactEntry artifact)
    {
        var ok = true;

        if (artifact.Rid is not null && string.IsNullOrWhiteSpace(artifact.Rid))
        {
            context.Error(
                "artifact-rid",
                $"sei '{name}' {entry.Version} has an empty rid",
                path);
            ok = false;
        }

        ValidateRelations(context, path, name, entry.Version, "artifact dependency", artifact.Dependencies);
        ValidateRelations(context, path, name, entry.Version, "artifact prerequisite", artifact.Prerequisites);
        ValidateRelations(context, path, name, entry.Version, "artifact optional peer", artifact.OptionalPeers);
        ValidateRelations(context, path, name, entry.Version, "artifact conflict", artifact.Conflicts);

        if (artifact.DependencyGroups is { } groups)
        {
            var seen = new List<DependencyGroup>();
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                if (group is null)
                {
                    context.Error(
                        "artifact-group",
                        $"sei '{name}' {entry.Version} has a null dependency group",
                        path);
                    ok = false;
                    continue;
                }

                var kind = $"group {index}";
                ValidateRelations(context, path, name, entry.Version, $"{kind} dependency", group.Dependencies);
                ValidateRelations(context, path, name, entry.Version, $"{kind} prerequisite", group.Prerequisites);
                ValidateRelations(context, path, name, entry.Version, $"{kind} optional peer", group.OptionalPeers);
                ValidateRelations(context, path, name, entry.Version, $"{kind} conflict", group.Conflicts);

                // Two groups that apply under identical conditions but
                // install different relations cannot be told apart by the
                // resolver; which one wins would depend on source drift and
                // environment. Reject that at packaging time (ADR-0045).
                // Unconditional groups are allowed: a single one is a
                // fallback, and two unconditional groups with identical
                // relations are exact duplicates, not a silent choice.
                if (seen.Any(existing =>
                    string.Equals(
                        DependencyGroups.ConditionKey(existing),
                        DependencyGroups.ConditionKey(group),
                        StringComparison.Ordinal)
                    && !string.Equals(
                        DependencyGroups.ResultKey(existing),
                        DependencyGroups.ResultKey(group),
                        StringComparison.Ordinal)))
                {
                    context.Error(
                        "artifact-group-ambiguous",
                        $"sei '{name}' {entry.Version} has dependency groups with the same conditions but different relations",
                        path);
                    ok = false;
                }

                seen.Add(group);
            }
        }

        if (artifact.Layout is not null && artifact.Layout != "root")
        {
            context.Error(
                "artifact-layout",
                $"sei '{name}' {entry.Version} has unsupported layout '{artifact.Layout}'",
                path);
            ok = false;
        }

        var source = artifact.Source;
        if (source is null)
        {
            context.Error(
                "artifact-source",
                $"sei '{name}' {entry.Version} has no artifact source",
                path);
            return false;
        }

        if (ExternalFiles.Count(new ArtifactManifest
        {
            ExternalFiles = artifact.ExternalFiles,
            ExternalArchives = artifact.ExternalArchives,
        }) > 0)
        {
            var declarationsValid = true;
            try
            {
                ExternalFiles.Validate(
                    new ArtifactManifest
                    {
                        ExternalFiles = artifact.ExternalFiles,
                        ExternalArchives = artifact.ExternalArchives,
                    },
                    $"sei '{name}' {entry.Version}",
                    "artifact-external");
            }
            catch (SeikoException ex)
            {
                context.Error("artifact-external", ex.Message, path);
                ok = false;
                declarationsValid = false;
            }

            var summary = artifact.External;
            if (summary is null)
            {
                context.Warning(
                    "artifact-external-summary",
                    $"sei '{name}' {entry.Version} has external files but no external summary",
                    path);
            }
            else if (!declarationsValid)
            {
                // Failed declarations may carry out-of-range values, so the
                // mirror would be compared against nonsense. The declaration
                // error already rejects the artifact (ADR-0032).
            }
            else
            {
                // Same full comparison the sync warning uses, so a verifier
                // pass cannot hide a wrong host or size the preview trusts
                // (ADR-0037).
                var manifestArtifact = new ArtifactManifest
                {
                    ExternalFiles = artifact.ExternalFiles,
                    ExternalArchives = artifact.ExternalArchives,
                };
                if (!ExternalFiles.SummaryMatches(summary, manifestArtifact))
                {
                    context.Error(
                        "artifact-external-summary",
                        $"sei '{name}' {entry.Version} external summary does not match the declarations",
                        path);
                    ok = false;
                }
            }
        }
        else if (artifact.External is not null)
        {
            context.Error(
                "artifact-external-summary",
                $"sei '{name}' {entry.Version} has an external summary but no external declarations",
                path);
            ok = false;
        }

        if (!IsValidArtifactUrl(source.Url))
        {
            context.Error(
                "artifact-url",
                $"sei '{name}' {entry.Version} has invalid artifact URL '{DescribeArtifactUrl(source.Url)}'",
                path);
            ok = false;
        }
        else if (IsLocalAbsoluteArtifact(source.Url))
        {
            context.Warning(
                "artifact-local",
                $"sei '{name}' {entry.Version} uses a machine-local artifact URL '{source.Url}'",
                path);
        }

        if (!Hashing.IsSha256(source.Sha256))
        {
            context.Error(
                "artifact-sha256",
                $"sei '{name}' {entry.Version} has invalid SHA-256 '{source.Sha256}'",
                path);
            ok = false;
        }

        if (source.Size <= 0 || source.Size > ArchiveLimits.MaxTotalBytes)
        {
            context.Error(
                "artifact-size",
                $"sei '{name}' {entry.Version} has invalid artifact size {source.Size}",
                path);
            ok = false;
        }

        return ok;
    }

    private static int CheckArtifacts(
        Context context,
        string? proxy,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "seiko-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var client = CreateClient(proxy);
            var checkedCount = 0;
            foreach (var reference in context.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = reference.Artifact.Source.Url;
                var safe = Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                    ? Urls.Redact(parsed)
                    : url;
                log?.Invoke($"checking {reference.Sei} {reference.Version}: {safe}");
                CheckArtifact(context, reference, client, temp, cancellationToken);
                checkedCount++;
            }

            return checkedCount;
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    private static void CheckArtifact(
        Context context,
        ArtifactRef reference,
        HttpClient client,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var source = reference.Artifact.Source;
        var path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            if (TryResolveLocal(context.Root, source.Url, out var local))
            {
                if (!File.Exists(local))
                {
                    context.Error(
                        "artifact-missing",
                        $"sei '{reference.Sei}' {reference.Version}: artifact not found: {local}",
                        reference.Path);
                    return;
                }

                var localLength = new FileInfo(local).Length;
                if (localLength != source.Size)
                {
                    context.Error(
                        "artifact-size",
                        $"sei '{reference.Sei}' {reference.Version}: expected {source.Size} bytes, got {localLength}",
                        reference.Path);
                    return;
                }

                using var input = File.OpenRead(local);
                CopyBounded(input, path, source.Size, cancellationToken);
            }
            else
            {
                Download(client, source.Url, path, source.Size, cancellationToken);
            }

            var length = new FileInfo(path).Length;
            if (length != source.Size)
            {
                context.Error(
                    "artifact-size",
                    $"sei '{reference.Sei}' {reference.Version}: expected {source.Size} bytes, got {length}",
                    reference.Path);
                return;
            }

            var actual = Hashing.Sha256File(path);
            if (!string.Equals(actual, source.Sha256, StringComparison.Ordinal))
            {
                context.Error(
                    "artifact-checksum",
                    $"sei '{reference.Sei}' {reference.Version}: expected SHA-256 {source.Sha256}, got {actual}",
                    reference.Path);
                return;
            }

            ValidateManifest(context, reference, path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArtifactSizeException ex)
        {
            context.Error(
                "artifact-size",
                $"sei '{reference.Sei}' {reference.Version}: {ex.Message}",
                reference.Path);
        }
        catch (Exception ex) when (ex is IOException
            or HttpRequestException
            or TaskCanceledException
            or InvalidDataException)
        {
            context.Error(
                "artifact-read",
                $"sei '{reference.Sei}' {reference.Version}: {ex.Message}",
                reference.Path);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    private static void Download(
        HttpClient client,
        string url,
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", SeikoHttp.UserAgent);
        using var response = client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .GetAwaiter()
            .GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var safe = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? Urls.Redact(parsed) : url;
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} for {safe}");
        }

        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new ArtifactSizeException(
                $"artifact response is {length} bytes; expected {maxBytes}");
        }

        using var input = response.Content.ReadAsStream(cancellationToken);
        // A server can send headers and then stall the body; the request
        // timeout does not cover this read, so the read itself must observe
        // cancellation and an idle deadline (ADR-0017's bounds).
        CopyBounded(input, path, maxBytes, cancellationToken);
    }

    private static TimeSpan BodyIdleTimeout => BodyIdleTimeoutOverride ?? DefaultBodyIdleTimeout;

    private static readonly TimeSpan DefaultBodyIdleTimeout = TimeSpan.FromSeconds(30);

    // Readable by tests; production keeps the default.
    internal static TimeSpan? BodyIdleTimeoutOverride { get; set; }

    private static void CopyBounded(Stream input, string path, long maxBytes, CancellationToken cancellationToken)
    {
        using var output = File.Create(path);
        var buffer = new byte[81920];
        var total = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = ReadWithIdleLimit(input, buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            if (total + read > maxBytes)
            {
                throw new ArtifactSizeException(
                    $"artifact response exceeds the expected {maxBytes} bytes");
            }

            output.Write(buffer, 0, read);
            total += read;
        }
    }

    private sealed class ArtifactSizeException(string message) : Exception(message);

    // Synchronous Read cannot be cancelled on every stream; racing the read
    // against the token also covers a stalled body without changing the
    // verifier's serial structure.
    private static int ReadWithIdleLimit(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = input.ReadAsync(buffer, cancellationToken).AsTask();
        bool finished;
        try
        {
            // Wait(timeout, token) throws OperationCanceledException when the
            // token fires, so cancellation is observed without waiting the
            // full budget.
            finished = read.Wait(BodyIdleTimeout, cancellationToken);
        }
        catch (AggregateException ex)
        {
            // Wait surfaces a failed read as AggregateException, but the
            // caller's catch list knows the real types (IOException,
            // HttpRequestException); rethrow the original.
            ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
            throw;
        }

        if (finished)
        {
            return read.GetAwaiter().GetResult();
        }

        throw new IOException($"artifact response stalled for {BodyIdleTimeout.TotalSeconds} s");
    }

    private static void ValidateManifest(Context context, ArtifactRef reference, string path)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            context.Error(
                "artifact-format",
                $"sei '{reference.Sei}' {reference.Version}: artifact is not a zip archive",
                reference.Path);
            return;
        }

        using (archive)
        {
            if (archive.Entries.Count > ArchiveLimits.MaxEntries)
            {
                context.Error(
                    "artifact-budget",
                    $"sei '{reference.Sei}' {reference.Version}: artifact has {archive.Entries.Count} entries; the limit is {ArchiveLimits.MaxEntries}",
                    reference.Path);
                return;
            }

            var declaredTotal = 0L;
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var collisions = new PathCollisions();
            foreach (var entry in archive.Entries)
            {
                declaredTotal += entry.Length;
                if (entry.Length > ArchiveLimits.MaxEntryBytes
                    || declaredTotal > ArchiveLimits.MaxTotalBytes)
                {
                    context.Error(
                        "artifact-budget",
                        $"sei '{reference.Sei}' {reference.Version}: artifact expands beyond the extraction limits",
                        reference.Path);
                    return;
                }

                var name = ArchiveLimits.NormalizeName(entry.FullName);
                if (name.Length == 0)
                {
                    continue;
                }

                if (ArchiveLimits.ExceedsEntryNameLimits(name))
                {
                    context.Error(
                        "artifact-path",
                        $"sei '{reference.Sei}' {reference.Version}: archive path is too long or too deep",
                        reference.Path);
                    return;
                }

                if (!StoreDigests.IsSafeRelativePath(name))
                {
                    context.Error(
                        "artifact-path",
                        $"sei '{reference.Sei}' {reference.Version}: unsafe archive path '{name}'",
                        reference.Path);
                    return;
                }

                if (!seenNames.Add(name))
                {
                    context.Error(
                        "artifact-duplicate",
                        $"sei '{reference.Sei}' {reference.Version}: duplicate archive path '{name}'",
                        reference.Path);
                    return;
                }

                collisions.Add(name);
            }

            if (collisions.TryFind(out var archiveCollision))
            {
                context.Error(
                    "artifact-path",
                    artifactCollision(reference, archiveCollision!),
                    reference.Path);
                return;
            }

            var manifests = archive.Entries
                .Where(entry => ArchiveLimits.NormalizeName(entry.FullName) == "sei.json")
                .ToList();
            if (manifests.Count == 0)
            {
                context.Error(
                    "manifest-missing",
                    $"sei '{reference.Sei}' {reference.Version}: artifact has no root sei.json",
                    reference.Path);
                return;
            }

            if (manifests.Count > 1)
            {
                context.Error(
                    "manifest-duplicate",
                    $"sei '{reference.Sei}' {reference.Version}: artifact has more than one root sei.json",
                    reference.Path);
                return;
            }

            if (manifests[0].Length > ArchiveLimits.MaxManifestBytes)
            {
                context.Error(
                    "manifest-budget",
                    $"sei '{reference.Sei}' {reference.Version}: sei.json exceeds the {ArchiveLimits.MaxManifestBytes}-byte limit",
                    reference.Path);
                return;
            }

            SeiManifest? manifest;
            try
            {
                using var stream = manifests[0].Open();
                manifest = JsonSerializer.Deserialize(stream, SeikoJsonContext.Default.SeiManifest);
            }
            catch (JsonException ex)
            {
                context.Error(
                    "manifest-invalid",
                    $"sei '{reference.Sei}' {reference.Version}: invalid sei.json: {ex.Message}",
                    reference.Path);
                return;
            }

            if (manifest is null)
            {
                context.Error(
                    "manifest-invalid",
                    $"sei '{reference.Sei}' {reference.Version}: empty sei.json",
                    reference.Path);
                return;
            }

            var prefix = $"sei '{reference.Sei}' {reference.Version}";
            if (manifest.SchemaVersion is < 2 or > SeiManifest.CurrentSchemaVersion)
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json schemaVersion {manifest.SchemaVersion}",
                    reference.Path);
            }
            else if (!string.Equals(manifest.Name, reference.Sei, StringComparison.Ordinal))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json declares name '{manifest.Name}'",
                    reference.Path);
            }
            else if (!NuGetVersion.TryParse(manifest.Version, out var manifestVersion)
                || !manifestVersion.Equals(reference.ParsedVersion))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json declares version '{manifest.Version}'",
                    reference.Path);
            }
            else if (manifest.Dependencies is null)
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json has a null dependency list",
                    reference.Path);
            }
            else if (!DependenciesEqual(manifest.Dependencies, reference.Dependencies))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json dependencies do not match the index",
                    reference.Path);
            }
            else if (manifest.Prerequisites is null
                || manifest.OptionalPeers is null
                || manifest.Conflicts is null)
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json has a null relation list",
                    reference.Path);
            }
            else if (!DependenciesEqual(manifest.Prerequisites, reference.Prerequisites)
                || !DependenciesEqual(manifest.OptionalPeers, reference.OptionalPeers)
                || !DependenciesEqual(manifest.Conflicts, reference.Conflicts))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json relations do not match the index",
                    reference.Path);
            }
            else if (manifest.Artifact is null)
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json has no artifact object",
                    reference.Path);
            }
            else if (!string.Equals(
                manifest.Artifact.Rid,
                reference.Artifact.Rid,
                StringComparison.OrdinalIgnoreCase))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json rid does not match the index artifact",
                    reference.Path);
            }
            else if (!DependenciesEqual(manifest.Artifact.Dependencies, reference.Artifact.Dependencies)
                || !DependenciesEqual(manifest.Artifact.Prerequisites, reference.Artifact.Prerequisites)
                || !DependenciesEqual(manifest.Artifact.OptionalPeers, reference.Artifact.OptionalPeers)
                || !DependenciesEqual(manifest.Artifact.Conflicts, reference.Artifact.Conflicts))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json artifact relations do not match the index artifact",
                    reference.Path);
            }
            else if (!string.Equals(
                NormalizeLayout(manifest.Artifact.Layout),
                NormalizeLayout(reference.Artifact.Layout),
                StringComparison.Ordinal))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json layout does not match the index artifact",
                    reference.Path);
            }
            else if (!ModesEqual(manifest.Artifact.Modes, reference.Artifact.Modes))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json modes do not match the index artifact",
                    reference.Path);
            }
            else if (manifest.SchemaVersion >= 4
                && !PreserveEqual(manifest.Artifact.Preserve, reference.Artifact.Preserve))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json preserve does not match the index artifact",
                    reference.Path);
            }
            else if (manifest.SchemaVersion >= 4
                && !DependencyGroups.Equal(
                    manifest.Artifact.DependencyGroups,
                    reference.Artifact.DependencyGroups))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json dependencyGroups do not match the index artifact",
                    reference.Path);
            }

            // A 3 manifest may carry external payload; validate it here so CI
            // catches bad URLs, paths and digests without fetching the bytes.
            if (ExternalFiles.Count(manifest.Artifact) > 0)
            {
                if (manifest.SchemaVersion == 2)
                {
                    context.Error(
                        "manifest-external",
                        $"{prefix}: external declarations require sei.json schemaVersion 3",
                        reference.Path);
                }

                var declarationsValid = true;
                try
                {
                    ExternalFiles.Validate(manifest.Artifact!, prefix, "manifest-external");
                }
                catch (SeikoException ex)
                {
                    context.Error("manifest-external", ex.Message, reference.Path);
                    declarationsValid = false;
                }

                // External targets must not collide with the archive's own
                // names under the same folded rules (ADR-0024). The archive
                // set already has no internal collisions, so a per-target
                // lookup is enough and stays linear.
                // A declaration that already failed may carry a target past
                // the length or depth limit; its per-prefix lookups would then
                // hash far more than the rejected string is worth, so the
                // group is skipped once one error is reported.
                var declared = (manifest.Artifact!.ExternalFiles ?? []).Select(file => file.Path)
                    .Concat((manifest.Artifact.ExternalArchives ?? [])
                        .SelectMany(archive => archive.Members)
                        .Select(member => member.Path));
                foreach (var target in declarationsValid ? declared : [])
                {
                    if (collisions.TryFindCollisionWith(target, out var targetCollision))
                    {
                        context.Error(
                            "manifest-external",
                            targetCollision!.Kind == PathCollisionKind.SamePath
                                ? $"{prefix}: external target path '{target}' also exists inside the artifact as '{targetCollision.Other}'"
                                : $"{prefix}: path '{targetCollision.Other}' is both a file and a directory of external target '{target}'",
                            reference.Path);
                    }
                }
            }

            if (manifest.Artifact?.Modes is not null)
            {
                var deployed = DeployedPaths(manifest, seenNames);
                foreach (var (file, text) in manifest.Artifact.Modes)
                {
                    if (file is null || !deployed.Contains(file))
                    {
                        context.Error(
                            "manifest-mode",
                            $"{prefix}: mode names '{file}', which the artifact does not deploy",
                            reference.Path);
                    }
                    else if (text is null || !FileModes.TryParse(text, out var value))
                    {
                        context.Error(
                            "manifest-mode",
                            $"{prefix}: invalid mode '{text}' for {file}",
                            reference.Path);
                    }
                    else if (FileModes.HasSpecialBits(value))
                    {
                        context.Error(
                            "manifest-mode",
                            $"{prefix}: mode '{text}' for {file} sets setuid/setgid/sticky",
                            reference.Path);
                    }
                }
            }

            if (manifest.Artifact?.DependencyGroups is { Count: > 0 }
                && manifest.SchemaVersion < 4)
            {
                context.Error(
                    "manifest-group",
                    $"{prefix}: dependencyGroups requires sei.json schemaVersion 4",
                    reference.Path);
            }

            if (manifest.Artifact?.Preserve is { Count: > 0 } preserve)
            {
                if (manifest.SchemaVersion < 4)
                {
                    context.Error(
                        "manifest-preserve",
                        $"{prefix}: preserve requires sei.json schemaVersion 4",
                        reference.Path);
                }

                var deployed = DeployedPaths(manifest, seenNames);
                foreach (var file in preserve)
                {
                    if (string.IsNullOrEmpty(file) || !deployed.Contains(file))
                    {
                        context.Error(
                            "manifest-preserve",
                            $"{prefix}: preserve names '{file}', which the artifact does not deploy",
                            reference.Path);
                    }
                }
            }
        }
    }

    // A mode or preserve entry names a deployed file; an entry that nothing
    // deploys is a typo that would otherwise be applied to nothing and never
    // reported. sei.json is skipped by deployment, so it counts as undeployed
    // here as well.
    private static HashSet<string> DeployedPaths(SeiManifest manifest, IEnumerable<string> seenNames)
    {
        var deployed = new HashSet<string>(
            seenNames.Where(name => name != "sei.json"),
            StringComparer.Ordinal);
        deployed.UnionWith((manifest.Artifact!.ExternalFiles ?? []).Select(file => file.Path));
        deployed.UnionWith((manifest.Artifact.ExternalArchives ?? [])
            .SelectMany(archive => archive.Members)
            .Select(member => member.Path));
        return deployed;
    }

    private static string artifactCollision(ArtifactRef reference, PathCollision collision)
        => collision.Kind == PathCollisionKind.SamePath
            ? $"sei '{reference.Sei}' {reference.Version}: archive path '{collision.Other}' differs only by case or Unicode normalization from another archive path"
            : $"sei '{reference.Sei}' {reference.Version}: archive path '{collision.Other}' is both a file and a directory of another archive path";

    private static bool DependenciesEqual(
        List<SeiDependency>? manifestDependencies,
        List<SeiDependency> indexDependencies)
    {
        if (manifestDependencies?.Any(dependency => dependency is null) == true)
        {
            return false;
        }

        var left = (manifestDependencies ?? [])
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .Select(dependency => $"{dependency.Name}|{dependency.Version}")
            .ToList();
        var right = indexDependencies
            .OrderBy(dependency => dependency.Name, StringComparer.Ordinal)
            .Select(dependency => $"{dependency.Name}|{dependency.Version}")
            .ToList();
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static bool ModesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var a = left ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var b = right ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return a.Count == b.Count
            && a.All(pair =>
                b.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    // Preserve order carries no meaning; duplicates stay visible because the
    // counts must match.
    private static bool PreserveEqual(List<string>? left, List<string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        return a.Count == b.Count
            && a.OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(
                    b.OrderBy(path => path, StringComparer.Ordinal),
                    StringComparer.Ordinal);
    }

    private static string? NormalizeLayout(string? layout)
        => string.IsNullOrEmpty(layout) ? "root" : layout;

    private static bool IsValidRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == "*")
        {
            return true;
        }

        return VersionRange.TryParse(text, out _);
    }

    private static bool IsValidArtifactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (IsRemote(url))
        {
            return Urls.TryGetHttp(url, out _);
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile;
        }

        if (url.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return url.Split('/', '\\').All(segment => segment != "..");
    }

    private static string DescribeArtifactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(empty)";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "file"
                ? Urls.Redact(uri)
                : uri.Scheme + ":...";
        }

        if (url.Contains("://", StringComparison.Ordinal))
        {
            return "(invalid URL)";
        }

        var end = url.IndexOfAny(['?', '#']);
        return end < 0 ? url : url[..end];
    }

    private static bool IsLocalAbsoluteArtifact(string url)
        => url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(url)
            || (url.Length >= 2 && char.IsAsciiLetter(url[0]) && url[1] == ':');

    private static bool IsRemote(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveLocal(string root, string url, out string path)
    {
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }

        if (IsRemote(url))
        {
            path = "";
            return false;
        }

        path = Path.GetFullPath(Path.Combine(root, url));
        return true;
    }

    private static HttpClient CreateClient(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ArtifactRef(
        string Sei,
        string Version,
        NuGetVersion ParsedVersion,
        List<SeiDependency> Dependencies,
        List<SeiDependency> Prerequisites,
        List<SeiDependency> OptionalPeers,
        List<SeiDependency> Conflicts,
        ArtifactEntry Artifact,
        string Path);

    private sealed class Context(string root)
    {
        public string Root { get; } = root;
        public List<IndexFinding> Findings { get; } = [];
        public List<ArtifactRef> Artifacts { get; } = [];
        public int Sei { get; set; }
        public int Versions { get; set; }
        public int ArtifactsCount { get; set; }

        public void Error(string code, string message, string? path = null)
            => Findings.Add(new IndexFinding("error", code, message, path));

        public void Warning(string code, string message, string? path = null)
            => Findings.Add(new IndexFinding("warning", code, message, path));

        public IndexVerifyResult ToResult(int checkedCount)
            => new(Root, Sei, Versions, ArtifactsCount, checkedCount, Findings);
    }
}
