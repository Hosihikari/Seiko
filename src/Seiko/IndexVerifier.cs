using System.IO.Compression;
using System.Net;
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
    public static IndexVerifyResult Verify(
        string directory,
        bool checkArtifacts = false,
        string? proxy = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
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

        return context.ToResult(checkedCount);
    }

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

            if (index.SchemaVersion != 1)
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

        if (index.SchemaVersion != 1)
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

        context.Sei++;
        if (index.Deprecated is not null && string.IsNullOrWhiteSpace(index.Deprecated))
        {
            context.Warning(
                "deprecated-empty",
                $"sei '{name}' has an empty deprecated message",
                relative);
        }

        var versions = index.Versions ?? [];
        if (versions.Count == 0)
        {
            context.Warning("sei-empty", $"sei '{name}' has no versions", relative);
        }

        var parsedVersions = new List<NuGetVersion>();
        foreach (var entry in versions)
        {
            if (entry is null)
            {
                context.Error("version-invalid", "version entry is null", relative);
                continue;
            }

            var version = ValidateVersion(context, relative, name, entry, parsedVersions);
            if (version is null)
            {
                continue;
            }

            parsedVersions.Add(version);
            context.Versions++;

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

            var selectors = new List<ArtifactEntry>();
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

                if (selectors.Any(existing => SelectorEquals(existing.Selectors, artifact.Selectors)))
                {
                    context.Error(
                        "artifact-ambiguous",
                        $"sei '{name}' {entry.Version} has duplicate artifact selectors",
                        relative);
                    continue;
                }

                selectors.Add(artifact);
                context.Artifacts.Add(new ArtifactRef(name, entry.Version, version, dependencies, artifact, relative));
            }
        }
    }

    private static NuGetVersion? ValidateVersion(
        Context context,
        string path,
        string name,
        SeiVersionEntry entry,
        List<NuGetVersion> parsedVersions)
    {
        if (!NuGetVersion.TryParse(entry.Version, out var version))
        {
            context.Error(
                "version-invalid",
                $"sei '{name}' has invalid version '{entry.Version}'",
                path);
            return null;
        }

        if (parsedVersions.Any(existing => existing.Equals(version)))
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

            if (advisory.Url is not null
                && (!Uri.TryCreate(advisory.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                context.Error(
                    "advisory-url",
                    $"sei '{name}' {entry.Version} has invalid advisory URL '{advisory.Url}'",
                    path);
            }
        }

        return version;
    }

    private static bool ValidateArtifact(
        Context context,
        string path,
        string name,
        SeiVersionEntry entry,
        ArtifactEntry artifact)
    {
        var ok = true;

        if (artifact.Layout is not null && artifact.Layout != "root")
        {
            context.Error(
                "artifact-layout",
                $"sei '{name}' {entry.Version} has unsupported layout '{artifact.Layout}'",
                path);
            ok = false;
        }

        if (artifact.Selectors is not null)
        {
            foreach (var (key, value) in artifact.Selectors)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    context.Error(
                        "artifact-selector",
                        $"sei '{name}' {entry.Version} has an empty selector",
                        path);
                    ok = false;
                    continue;
                }

                if (!key.Equals("rid", StringComparison.OrdinalIgnoreCase)
                    && !SeiName.IsValid(value))
                {
                    context.Error(
                        "artifact-selector",
                        $"sei '{name}' {entry.Version} has invalid selector value '{value}' for '{key}'",
                        path);
                    ok = false;
                }
            }
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

        if (!IsValidArtifactUrl(source.Url))
        {
            context.Error(
                "artifact-url",
                $"sei '{name}' {entry.Version} has invalid artifact URL '{source.Url}'",
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

        if (!IsSha256(source.Sha256))
        {
            context.Error(
                "artifact-sha256",
                $"sei '{name}' {entry.Version} has invalid SHA-256 '{source.Sha256}'",
                path);
            ok = false;
        }

        if (source.Size <= 0)
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

                File.Copy(local, path);
            }
            else
            {
                Download(client, source.Url, path, cancellationToken);
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

    private static void Download(HttpClient client, string url, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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

        using var input = response.Content.ReadAsStream(cancellationToken);
        using var output = File.Create(path);
        input.CopyTo(output);
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

                if (name.Split('/').Any(segment => segment == "..")
                    || name.StartsWith('/')
                    || (name.Length >= 2 && name[1] == ':'))
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
            if (manifest.SchemaVersion != 1)
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
            else if (!DependenciesEqual(manifest.Dependencies, reference.Dependencies))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json dependencies do not match the index",
                    reference.Path);
            }
            else if (!SelectorEquals(manifest.Selectors, reference.Artifact.Selectors))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json selectors do not match the index artifact",
                    reference.Path);
            }
            else if (!string.Equals(
                NormalizeLayout(manifest.Layout),
                NormalizeLayout(reference.Artifact.Layout),
                StringComparison.Ordinal))
            {
                context.Error(
                    "manifest-mismatch",
                    $"{prefix}: sei.json layout does not match the index artifact",
                    reference.Path);
            }

            if (manifest.Modes is not null)
            {
                foreach (var (file, text) in manifest.Modes)
                {
                    if (text is null || !FileModes.TryParse(text, out var value))
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
        }
    }

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

    private static bool SelectorEquals(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidArtifactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (IsRemote(url))
        {
            return true;
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile;
        }

        return url.Split('/', '\\').All(segment => segment != "..");
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
