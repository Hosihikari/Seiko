using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Seiko;

public sealed class HttpSource : ISeiSource
{
    private readonly HttpClient _client = new();
    private readonly Uri _baseUri;
    private readonly string? _token;
    private readonly Dictionary<string, SeiIndex?> _cache = new(StringComparer.Ordinal);

    public string Id { get; }
    public IndexRoot Index { get; }

    public HttpSource(SourceConfig config)
    {
        Id = config.Id;
        if (!Uri.TryCreate(config.Index, UriKind.Absolute, out var indexUri)
            || (indexUri.Scheme != Uri.UriSchemeHttp && indexUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SeikoException($"source '{Id}': invalid http index '{config.Index}'");
        }

        _baseUri = new Uri(indexUri, ".");
        _token = ResolveToken(config);
        Index = FetchIndex(indexUri);
    }

    public SeiIndex? GetSei(string name)
    {
        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var uri = new Uri(_baseUri, $"sei/{name}.json");
        using var response = Send(uri);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _cache[name] = null;
            return null;
        }

        response.EnsureSuccessStatusCode();
        var sei = JsonSerializer.Deserialize(ReadAllBytes(response), SeikoJsonContext.Default.SeiIndex)
            ?? throw new SeikoException($"source '{Id}': invalid sei index for {name}");

        if (sei.SchemaVersion != 1 || !string.Equals(sei.Name, name, StringComparison.Ordinal))
        {
            throw new SeikoException($"source '{Id}': invalid sei index for {name}");
        }

        _cache[name] = sei;
        return sei;
    }

    public string ResolveArtifact(ArtifactLocation source) => new Uri(_baseUri, source.Url).AbsoluteUri;

    public void DownloadArtifact(ArtifactLocation source, string destination)
    {
        using var response = Send(new Uri(_baseUri, source.Url));
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        using var file = File.Create(destination);
        stream.CopyTo(file);
    }

    private IndexRoot FetchIndex(Uri uri)
    {
        using var response = Send(uri);
        response.EnsureSuccessStatusCode();
        var index = JsonSerializer.Deserialize(ReadAllBytes(response), SeikoJsonContext.Default.IndexRoot)
            ?? throw new SeikoException($"source '{Id}': invalid index");

        if (index.SchemaVersion != 1)
        {
            throw new SeikoException($"source '{Id}': unsupported index schemaVersion {index.SchemaVersion}");
        }

        return index;
    }

    private HttpResponseMessage Send(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return _client.Send(request);
    }

    private static byte[] ReadAllBytes(HttpResponseMessage response)
    {
        using var stream = response.Content.ReadAsStream();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
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
            throw new SeikoException($"source '{config.Id}': tokenEnv '{config.TokenEnv}' is not set");
        }

        return token;
    }
}
