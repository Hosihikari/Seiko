namespace Seiko;

public sealed class DownloadSession : IDisposable
{
    public const int DefaultJobs = 4;
    public const int MaxJobs = 64;

    public DownloadSession(
        int jobs,
        CancellationToken cancellationToken,
        long chunkThreshold = 32L * 1024 * 1024)
    {
        if (jobs < 1 || jobs > MaxJobs)
        {
            throw new SeikoException($"jobs must be between 1 and {MaxJobs}", "usage");
        }

        if (chunkThreshold <= 0)
        {
            throw new SeikoException("chunk threshold must be positive", "usage");
        }

        Jobs = jobs;
        CancellationToken = cancellationToken;
        ChunkThreshold = chunkThreshold;
        HttpSlots = new SemaphoreSlim(jobs, jobs);
        ExtractSlots = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, 4));
    }

    public int Jobs { get; }
    public CancellationToken CancellationToken { get; }
    public long ChunkThreshold { get; }
    public SemaphoreSlim HttpSlots { get; }
    public SemaphoreSlim ExtractSlots { get; }

    public void Dispose()
    {
        HttpSlots.Dispose();
        ExtractSlots.Dispose();
    }
}
