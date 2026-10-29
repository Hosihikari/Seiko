using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Seiko.Tests;

internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly string _root;

    public TestHttpServer(string root)
    {
        _root = Path.GetFullPath(root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}/";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client), _cts.Token);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            var parts = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = parts?.ElementAtOrDefault(0) ?? "GET";
            var path = parts?.ElementAtOrDefault(1) ?? "/";
            if (path == "/")
            {
                path = "/index.json";
            }

            var relative = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(_root, relative));
            if (!file.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(file))
            {
                await WriteResponseAsync(stream, "404 Not Found", [], null);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(file);
            var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
            var extra = new Dictionary<string, string>
            {
                ["Accept-Ranges"] = "bytes",
                ["ETag"] = etag,
            };

            if (method == "HEAD")
            {
                await WriteResponseAsync(stream, "200 OK", [], extra, bytes.Length);
                return;
            }

            if (headers.TryGetValue("Range", out var range)
                && TryParseRange(range, bytes.Length, out var start, out var end))
            {
                var chunk = bytes[start..(end + 1)];
                extra["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                await WriteResponseAsync(stream, "206 Partial Content", chunk, extra);
                return;
            }

            await WriteResponseAsync(stream, "200 OK", bytes, extra);
        }
    }

    private static bool TryParseRange(string value, int length, out int start, out int end)
    {
        start = 0;
        end = length - 1;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = value["bytes=".Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var first = spec[..dash];
        var second = spec[(dash + 1)..];
        if (first.Length == 0)
        {
            if (!int.TryParse(second, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, length - suffix);
            return true;
        }

        if (!int.TryParse(first, out start))
        {
            return false;
        }

        if (second.Length > 0)
        {
            if (!int.TryParse(second, out end))
            {
                return false;
            }
        }

        return start >= 0 && start < length && end >= start;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        string status,
        byte[] body,
        Dictionary<string, string>? extra,
        int? contentLength = null)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength ?? body.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                builder.Append(key).Append(": ").Append(value).Append("\r\n");
            }
        }

        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()));
        if (body.Length > 0)
        {
            await stream.WriteAsync(body);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cts.Dispose();
    }
}
