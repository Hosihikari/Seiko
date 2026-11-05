namespace Seiko;

public sealed record PlannedChange(
    string Action,
    string Name,
    string? From,
    string? To,
    string? Source,
    string? Artifact);

public sealed record PreviewBlock(string Code, string Message);

public sealed record PreviewDiagnostic(string Name, string Kind, string Message);

public sealed class PreviewResult
{
    public IReadOnlyList<PlannedChange> Changes { get; init; } = [];
    public IReadOnlyList<PreviewBlock> Blocked { get; init; } = [];
    public IReadOnlyList<PreviewDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    // External payload of the planned changes: hosts and item count, so the
    // user sees third-party downloads before applying. Never fetched here.
    public IReadOnlyList<string> ExternalHosts { get; init; } = [];
    public int ExternalItems { get; init; }
    public long ExternalBytes { get; init; }
}
