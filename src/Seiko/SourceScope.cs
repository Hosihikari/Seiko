namespace Seiko;

// A source that stands in front of another one (the offline probe does) and
// lets callers reach the wrapped instance when they need its exact type.
internal interface ISourceWrapper
{
    ISeiSource InnerSource { get; }
}


// Owns one command's sources and, optionally, its download session. Add and
// Remove pre-resolve, then sync again; handing the same scope to both keeps
// the request caches warm instead of fetching the same metadata twice.
internal sealed class SourceScope : IDisposable
{
    private DownloadSession? _session;

    public SourceScope(IReadOnlyList<ISeiSource> sources, DownloadSession? session = null)
    {
        Sources = sources;
        _session = session;
    }

    // Filled after construction: the scope can exist, and own the session,
    // before the sources do.
    public IReadOnlyList<ISeiSource> Sources { get; set; } = [];

    // Takes over a session created before the scope existed; the scope now
    // releases it.
    public void Own(DownloadSession session) => _session = session;

    public DownloadSession? Session => _session;

    public void Dispose()
    {
        foreach (var source in Sources)
        {
            (source as IDisposable)?.Dispose();
        }

        _session?.Dispose();
    }
}
