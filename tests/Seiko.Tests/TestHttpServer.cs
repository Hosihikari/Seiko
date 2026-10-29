using System.Net;
using System.Net.Sockets;
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
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
            }

            var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            if (path == "/")
            {
                path = "/index.json";
            }

            var relative = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(_root, relative));
            if (!file.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(file))
            {
                await WriteResponseAsync(stream, "404 Not Found", []);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(file);
            await WriteResponseAsync(stream, "200 OK", bytes);
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
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
