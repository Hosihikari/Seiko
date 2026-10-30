using System.Text.Json;

namespace Seiko;

internal sealed class DeploymentTransaction
{
    private const string JournalName = "transaction.json";
    private const string BackupName = "transaction";

    private readonly string _root;
    private readonly string _journalPath;
    private readonly string _backupDirectory;
    private readonly DeploymentJournal _journal = new();
    private readonly Dictionary<string, DeploymentJournalEntry> _entries = new(StringComparer.Ordinal);

    private DeploymentTransaction(string root, string stateDirectory)
    {
        _root = Path.GetFullPath(root);
        _journalPath = Path.Combine(stateDirectory, JournalName);
        _backupDirectory = Path.Combine(stateDirectory, BackupName);
        Directory.CreateDirectory(_backupDirectory);
    }

    public static DeploymentTransaction Begin(string root, string stateDirectory)
    {
        EnsureStateDirectory(root, stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        var journalPath = Path.Combine(stateDirectory, JournalName);
        if (File.Exists(journalPath))
        {
            throw new SeikoException(
                "an incomplete deployment transaction exists; run `sk sync` to recover it",
                "deploy");
        }

        var backup = Path.Combine(stateDirectory, BackupName);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        return new DeploymentTransaction(root, stateDirectory);
    }

    public void WillChange(string relative)
    {
        var normalized = Normalize(relative);
        if (_entries.ContainsKey(normalized))
        {
            return;
        }

        PathSafety.EnsureNoLinks(_root, normalized);
        var fullPath = Resolve(_root, normalized);
        if (Directory.Exists(fullPath))
        {
            throw new SeikoException($"deployment path is a directory: {normalized}", "deploy");
        }

        var entry = new DeploymentJournalEntry
        {
            Path = normalized,
            Existed = File.Exists(fullPath),
        };
        if (entry.Existed)
        {
            entry.UnixMode = GetMode(fullPath);
            var backup = Resolve(_backupDirectory, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(fullPath, backup, overwrite: true);
        }

        _entries[normalized] = entry;
        _journal.Entries.Add(entry);
    }

    public void SetNewGeneration(string relative)
    {
        var normalized = Normalize(relative);
        PathSafety.EnsureNoLinks(_root, normalized);
        _journal.NewGenerationPath = normalized;
    }

    public void WriteJournal()
    {
        WriteJournalFile(_journal);
    }

    public void Commit()
    {
        _journal.Committed = true;
        WriteJournalFile(_journal);
        Cleanup(_journalPath, _backupDirectory);
    }

    public void RollbackAfterFailure(Exception original)
    {
        try
        {
            Restore(_root, _backupDirectory, _journal);
            Cleanup(_journalPath, _backupDirectory);
        }
        catch (Exception rollback)
        {
            throw new SeikoException(
                $"deployment failed ({original.Message}) and rollback also failed ({rollback.Message}); "
                + "the transaction journal is kept for the next sync",
                "deploy",
                rollback);
        }
    }

    public static void Recover(string root, string stateDirectory)
    {
        EnsureStateDirectory(root, stateDirectory);
        var journalPath = Path.Combine(stateDirectory, JournalName);
        var backupDirectory = Path.Combine(stateDirectory, BackupName);
        if (!File.Exists(journalPath))
        {
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }

            return;
        }

        DeploymentJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize(
                    File.ReadAllBytes(journalPath),
                    SeikoJsonContext.Default.DeploymentJournal)
                ?? throw new SeikoException("invalid deployment transaction journal", "deploy");
        }
        catch (JsonException ex)
        {
            throw new SeikoException($"invalid deployment transaction journal: {ex.Message}", "deploy", ex);
        }

        if (journal.SchemaVersion != 1)
        {
            throw new SeikoException(
                $"unsupported deployment transaction schema {journal.SchemaVersion}",
                "deploy");
        }

        if (!journal.Committed)
        {
            Restore(Path.GetFullPath(root), Path.GetFullPath(backupDirectory), journal);
        }

        Cleanup(journalPath, backupDirectory);
    }

    private static void Restore(string root, string backupDirectory, DeploymentJournal journal)
    {
        var rootFull = Path.GetFullPath(root);
        var backupFull = Path.GetFullPath(backupDirectory);
        for (var i = journal.Entries.Count - 1; i >= 0; i--)
        {
            var entry = journal.Entries[i];
            var relative = Normalize(entry.Path);
            PathSafety.EnsureNoLinks(rootFull, relative);
            var destination = Resolve(rootFull, relative);
            if (entry.Existed)
            {
                PathSafety.EnsureNoLinks(backupFull, relative);
                var backup = Resolve(backupFull, relative);
                if (!File.Exists(backup))
                {
                    throw new SeikoException($"rollback backup is missing: {relative}", "deploy");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backup, destination, overwrite: true);
                SetMode(destination, entry.UnixMode);
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }

        if (journal.NewGenerationPath is { } generation)
        {
            var normalized = Normalize(generation);
            PathSafety.EnsureNoLinks(rootFull, normalized);
            var path = Resolve(rootFull, normalized);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static void Cleanup(string journalPath, string backupDirectory)
    {
        try
        {
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A committed journal can be cleaned on the next sync.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteJournalFile(DeploymentJournal journal)
    {
        var temp = _journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(
            temp,
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));
        File.Move(temp, _journalPath, overwrite: true);
    }

    private static void EnsureStateDirectory(string root, string stateDirectory)
    {
        var relative = Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(stateDirectory))
            .Replace('\\', '/');
        PathSafety.EnsureNoLinks(root, relative);
    }

    private static string Normalize(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            throw new SeikoException($"invalid deployment path: {relative}", "deploy");
        }

        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new SeikoException($"invalid deployment path: {relative}", "deploy");
        }

        return normalized;
    }

    private static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SeikoException($"deployment path escapes the install root: {relative}", "deploy");
        }

        return full;
    }

    private static int? GetMode(string path)
        => OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path);

    private static void SetMode(string path, int? mode)
    {
        if (OperatingSystem.IsWindows() || mode is null)
        {
            return;
        }

        File.SetUnixFileMode(path, (UnixFileMode)mode.Value);
    }
}
