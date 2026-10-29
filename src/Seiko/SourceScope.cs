namespace Seiko;

internal sealed class SourceScope : IDisposable
{
    private readonly IReadOnlyList<ISeiSource> _sources;

    public SourceScope(IReadOnlyList<ISeiSource> sources)
    {
        _sources = sources;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            (source as IDisposable)?.Dispose();
        }
    }
}
