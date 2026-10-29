namespace Seiko;

public enum SyncPhase
{
    Resolved,
    Downloading,
    Completed,
    Deploying,
}

public sealed record SyncProgress(
    SyncPhase Phase,
    string? Name,
    long Downloaded,
    long Total,
    int SeiCompleted,
    int SeiTotal);
