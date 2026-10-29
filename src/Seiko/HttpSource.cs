using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Seiko;

public sealed class HttpSource : ISeiSource, IDisposable
{
    private const int MaxAttempts = 4;
    private const int MaxSegments = 4;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly string? _token;
    private readonly DownloadSession _session;
    private readonly Dictionary<string, SeiIndex?> _cache = new(StringComparer.Ordinal);

    public string Id { get; }
    public IndexRoot Index { get; }

    public void Dispose() => _client.Dispose();

    public HttpSource(SourceConfig config, string? proxy, DownloadSession session)
    {
        Id = config.Id;
        _session = session;
        if (!Uri.TryCreate(config.Index, UriKind.Absolute, out var indexUri)
            || (indexUri.Scheme != Uri.UriSchemeHttp && indexUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SeikoException($"source '{Id}': invalid http index '{config.Index}'", "config");
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _baseUri = new Uri(indexUri, ".");
        _token = ResolveToken(config);
        Index = FetchIndex(indexUri);
    }

    public SeiIndex? GetSei(string name) => GetSeiAsync(name).GetAwaiter().GetResult();

    private async Task<SeiIndex?> GetSeiAsync(string name)
    {
        if (!SeiName.IsValid(name))
        {
            throw new SeikoException($"source '{Id}': invalid sei name '{name}'", "source");
        }

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var uri = new Uri(_baseUri, $"sei/{name}.json");
        await _session.HttpSlots.WaitAsync(_session.CancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await SendWithRetryAsync(
                () => NewRequest(HttpMethod.Get, uri),
                "source",
                $"GET {SafeUri(uri)}",
                _session.CancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache[name] = null;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SeikoException(
                    $"source '{Id}': HTTP {(int)response.StatusCode} for {uri}",
                    "source");
            }

            var bytes = await ReadAllBytesAsync(response, _session.CancellationToken).ConfigureAwait(false);
            var sei = JsonSerializer.Deserialize(bytes, SeikoJsonContext.Default.SeiIndex)
                ?? throw new SeikoException($"source '{Id}': invalid sei index for {name}", "source");

            if (sei.SchemaVersion != 1 || !string.Equals(sei.Name, name, StringComparison.Ordinal))
            {
                throw new SeikoException($"source '{Id}': invalid sei index for {name}", "source");
            }

            _cache[name] = sei;
            return sei;
        }
        finally
        {
            _session.HttpSlots.Release();
        }
    }

    public string ResolveArtifact(ArtifactLocation source) => new Uri(_baseUri, source.Url).AbsoluteUri;

    public async Task DownloadAsync(
        string name,
        ArtifactLocation artifact,
        string directory,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        var uri = ResolveArtifact(artifact);
        var statePath = Path.Combine(directory, "meta.json");
        var artifactPath = Path.Combine(directory, "artifact");
        var remote = await GetRemoteInfoAsync(uri, artifact, session, cancellationToken).ConfigureAwait(false);
        var size = artifact.Size > 0 ? artifact.Size : remote.Size;
        var useChunks = size > session.ChunkThreshold && remote.AcceptRanges;
        var segmentCount = useChunks ? MaxSegments : 1;
        var segmentSize = size > 0 ? (size + segmentCount - 1) / segmentCount : 0;

        var state = PartialArtifactStateFile.Read(statePath);
        var reusable = state is not null
            && state.SchemaVersion == 1
            && string.Equals(state.Url, uri, StringComparison.Ordinal)
            && state.Size == size
            && state.SegmentCount == segmentCount
            && state.SegmentSize == segmentSize
            && string.Equals(state.ETag, remote.ETag, StringComparison.Ordinal)
            && string.Equals(state.LastModified, remote.LastModified, StringComparison.Ordinal)
            && PartialArtifactStateFile.HasValidator(state);

        if (!reusable)
        {
            PartialArtifactStateFile.ClearDirectory(directory);
            state = null;
        }

        state ??= new PartialArtifactState
        {
            Url = uri,
            Size = size,
            ETag = remote.ETag,
            LastModified = remote.LastModified,
            SegmentCount = segmentCount,
            SegmentSize = segmentSize,
        };
        PartialArtifactStateFile.Write(statePath, state);
        session.Log?.Invoke($"{name}: downloading {SafeUri(new Uri(uri))} ({size} bytes, {state.SegmentCount} segment(s))");

        long downloaded = 0;
        for (var i = 0; i < state.SegmentCount; i++)
        {
            var segmentPath = Path.Combine(directory, $"seg-{i}");
            if (File.Exists(segmentPath))
            {
                downloaded += new FileInfo(segmentPath).Length;
            }
        }

        session.ReportDownloading(name, downloaded, size);
        void OnRead(int bytes)
        {
            var current = Interlocked.Add(ref downloaded, bytes);
            session.ReportDownloading(name, current, size);
        }

        var segments = new Task[state.SegmentCount];
        for (var i = 0; i < segments.Length; i++)
        {
            var index = i;
            segments[i] = DownloadSegmentAsync(uri, state, index, directory, session, OnRead, cancellationToken);
        }

        await Task.WhenAll(segments).ConfigureAwait(false);
        await AssembleAsync(state, directory, artifactPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteInfo> GetRemoteInfoAsync(
        string uri,
        ArtifactLocation artifact,
        DownloadSession session,
        CancellationToken cancellationToken)
    {
        await session.HttpSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await SendWithRetryAsync(
                () => NewRequest(HttpMethod.Head, new Uri(uri)),
                "download",
                $"HEAD {SafeUri(new Uri(uri))}",
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SeikoException($"artifact not found: {uri}", "download");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RemoteInfo(0, false, null, null);
            }

            var acceptsRanges = response.Headers.AcceptRanges
                .Any(value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase));
            var size = response.Content.Headers.ContentLength ?? 0;
            if (artifact.Size > 0)
            {
                size = artifact.Size;
            }

            return new RemoteInfo(
                size,
                acceptsRanges,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"));
        }
        finally
        {
            session.HttpSlots.Release();
        }
    }

    private async Task DownloadSegmentAsync(
        string uri,
        PartialArtifactState state,
        int index,
        string directory,
        DownloadSession session,
        Action<int> onRead,
        CancellationToken cancellationToken)
    {
        var segmentPath = Path.Combine(directory, $"seg-{index}");
        var start = index * state.SegmentSize;
        var hasRange = state.Size > 0;
        var end = hasRange
            ? Math.Min(start + state.SegmentSize - 1, state.Size - 1)
            : -1;
        var expectedLength = end >= 0 ? end - start + 1 : -1;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var have = File.Exists(segmentPath) ? new FileInfo(segmentPath).Length : 0;
            if (expectedLength >= 0 && have == expectedLength)
            {
                return;
            }

            if (expectedLength >= 0 && have > expectedLength)
            {
                File.Delete(segmentPath);
                have = 0;
            }

            try
            {
                await session.HttpSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var usesRange = end >= 0 && (state.SegmentCount > 1 || have > 0);
                    using var response = await SendWithRetryAsync(
                        () => CreateRangeRequest(uri, state, start, end, have, usesRange),
                        "download",
                        $"GET {SafeUri(new Uri(uri))} [{index}]",
                        cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                        && expectedLength >= 0 && have == expectedLength)
                    {
                        return;
                    }

                    if (usesRange && response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        if (state.SegmentCount == 1 && have > 0 && response.StatusCode == HttpStatusCode.OK)
                        {
                            File.Delete(segmentPath);
                        }

                        throw new SeikoException(
                            $"server did not honor Range for {uri} (HTTP {(int)response.StatusCode})",
                            "download");
                    }

                    await AppendBodyAsync(response, segmentPath, onRead, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    session.HttpSlots.Release();
                }

                if (expectedLength >= 0 && new FileInfo(segmentPath).Length != expectedLength)
                {
                    throw new SeikoException($"short segment for {uri}", "download");
                }

                return;
            }
            catch (SeikoException) when (attempt < MaxAttempts)
            {
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private HttpRequestMessage CreateRangeRequest(
        string uri,
        PartialArtifactState state,
        long start,
        long end,
        long have,
        bool usesRange)
    {
        var request = NewRequest(HttpMethod.Get, new Uri(uri));
        if (!usesRange)
        {
            return request;
        }

        request.Headers.Range = new RangeHeaderValue(start + have, end);
        var validator = state.ETag ?? state.LastModified;
        if (have > 0 && validator is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(validator);
        }

        return request;
    }

    private static async Task AppendBodyAsync(
        HttpResponseMessage response,
        string segmentPath,
        Action<int> onRead,
        CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            segmentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await ReadWithTimeoutAsync(body, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onRead(read);
        }
    }

    private static async Task AssembleAsync(
        PartialArtifactState state,
        string directory,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(artifactPath))
        {
            File.Delete(artifactPath);
        }

        await using var output = new FileStream(
            artifactPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        for (var i = 0; i < state.SegmentCount; i++)
        {
            var segmentPath = Path.Combine(directory, $"seg-{i}");
            await using var input = new FileStream(
                segmentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadAllBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await ReadWithTimeoutAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            memory.Write(buffer, 0, read);
        }
    }

    private static async Task<int> ReadWithTimeoutAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IdleTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SeikoException("download read timed out", "download");
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string errorCode,
        string description,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(IdleTimeout);
                response = await _client.SendAsync(
                    requestFactory(),
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                _session.Log?.Invoke($"{description}: timed out, retrying (attempt {attempt})");
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _session.Log?.Invoke($"{description}: {ex.Message}, retrying (attempt {attempt})");
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SeikoException("request timed out", errorCode);
            }
            catch (HttpRequestException ex)
            {
                throw new SeikoException(ex.Message, errorCode, ex);
            }

            if (IsRetryable(response.StatusCode) && attempt < MaxAttempts)
            {
                var delay = RetryDelay(response, attempt);
                _session.Log?.Invoke(
                    $"{description}: HTTP {(int)response.StatusCode}, retrying (attempt {attempt})");
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)status >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
            }
        }

        return Backoff(attempt);
    }

    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))
            + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    private HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    private IndexRoot FetchIndex(Uri uri)
    {
        var bytes = FetchBytes(uri);
        var index = JsonSerializer.Deserialize(bytes, SeikoJsonContext.Default.IndexRoot)
            ?? throw new SeikoException($"source '{Id}': invalid index", "source");
        if (index.SchemaVersion != 1)
        {
            throw new SeikoException(
                $"source '{Id}': unsupported index schemaVersion {index.SchemaVersion}",
                "source");
        }

        return index;
    }

    private byte[] FetchBytes(Uri uri)
    {
        _session.HttpSlots.Wait();
        try
        {
            using var response = SendWithRetryAsync(
                () => NewRequest(HttpMethod.Get, uri),
                "source",
                $"GET {SafeUri(uri)}",
                CancellationToken.None).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new SeikoException(
                    $"source '{Id}': HTTP {(int)response.StatusCode} for {uri}",
                    "source");
            }

            return ReadAllBytesAsync(response, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            _session.HttpSlots.Release();
        }
    }

    private static string? ResolveToken(SourceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.TokenEnv))
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable(config.TokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SeikoException($"source '{config.Id}': tokenEnv '{config.TokenEnv}' is not set", "config");
        }

        return token;
    }

    private static string SafeUri(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    private sealed record RemoteInfo(long Size, bool AcceptRanges, string? ETag, string? LastModified);
}
