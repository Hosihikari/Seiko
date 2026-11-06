namespace Seiko;

// Owns one command's sources and, optionally, its download session. Add and
// Remove pre-resolve, then sync again; handing the same scope to both keeps
// the request caches warm instead of fetching the same metadata twice.
internal sealed class SourceScope : IDisposable
{
    private readonly IReadOnlyList<ISeiSource> _sources;
    private readonly DownloadSession? _session;

    public SourceScope(IReadOnlyList<ISeiSource> sources, DownloadSession? session = null)
    {
        _sources = sources;
        _session = session;
    }

    public IReadOnlyList<ISeiSource> Sources => _sources;

    public DownloadSession? Session => _session;

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            (source as IDisposable)?.Dispose();
        }

        _session?.Dispose();
    }
}
