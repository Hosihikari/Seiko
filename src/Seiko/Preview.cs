namespace Seiko;

public sealed record PlannedChange(
    string Action,
    string Name,
    string? From,
    string? To,
    string? Source,
    string? Artifact,
    // False for a local file: nothing cross-checked it against an index entry
    // (ADR-0044). Present regardless of --diagnostics.
    bool IndexVerified = true);

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
    // A resolved artifact whose index entry carries no external summary is
    // unknown, not zero (ADR-0037); it is listed rather than silently counted.
    public IReadOnlyList<string> ExternalHosts { get; init; } = [];
    public IReadOnlyList<string> ExternalUnknown { get; init; } = [];
    public int ExternalItems { get; init; }
    public long ExternalBytes { get; init; }
}
