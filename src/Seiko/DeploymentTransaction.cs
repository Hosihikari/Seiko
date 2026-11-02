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

    public void WillChange(string relative, bool allowDirectory = false)
    {
        var normalized = Normalize(relative);
        if (_entries.ContainsKey(normalized))
        {
            return;
        }

        var fullPath = Resolve(_root, normalized);
        if (Directory.Exists(fullPath) && !allowDirectory)
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

    public void WillCreateTemporary(string relative)
    {
        var normalized = Normalize(relative);
        _journal.TemporaryPaths ??= [];
        if (!_journal.TemporaryPaths.Contains(normalized, StringComparer.Ordinal))
        {
            _journal.TemporaryPaths.Add(normalized);
        }
    }

    public void SetNewGeneration(string relative)
    {
        var normalized = Normalize(relative);
        PathSafety.EnsureNoLinks(_root, normalized);
        _journal.NewGenerationPath = normalized;
    }

    public void WriteJournal()
    {
        WriteJournalFile(_journalPath, _journal);
    }

    public void Commit()
    {
        _journal.Committed = true;
        try
        {
            WriteJournalFile(_journalPath, _journal);
        }
        catch
        {
            // The on-disk journal is still uncommitted; rollback must see the
            // same state even though this in-memory instance is reused.
            _journal.Committed = false;
            throw;
        }

        Cleanup(_journalPath, _backupDirectory);
    }

    public void RollbackAfterFailure(Exception original)
    {
        try
        {
            Restore(_root, _backupDirectory, _journal, _journalPath);
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

        var rootFull = Path.GetFullPath(root);
        var backupFull = Path.GetFullPath(backupDirectory);
        var backupRelative = Path.GetRelativePath(rootFull, backupFull).Replace('\\', '/');
        PathSafety.EnsureNoLinks(rootFull, backupRelative);

        if (!journal.Committed)
        {
            Restore(rootFull, backupFull, journal, journalPath);
        }

        Cleanup(journalPath, backupDirectory);
    }

    private static void Restore(
        string root,
        string backupDirectory,
        DeploymentJournal journal,
        string journalPath)
    {
        var rootFull = Path.GetFullPath(root);
        var backupFull = Path.GetFullPath(backupDirectory);
        ValidateJournalPaths(rootFull, backupFull, journal);
        foreach (var temporary in journal.TemporaryPaths ?? [])
        {
            var normalized = Normalize(temporary);
            var path = Resolve(rootFull, normalized);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // This path only handles an uncommitted transaction. Persist that
        // state before any restore work, and register every temporary in one
        // journal write instead of rewriting the whole journal per file.
        var wasCommitted = journal.Committed;
        journal.Committed = false;
        journal.TemporaryPaths = [];
        var restoreTemporaries = PlanRestoreTemporaries(rootFull, journal);
        if (restoreTemporaries.Count > 0 || journal.Entries.Count > 0 || wasCommitted)
        {
            WriteJournalFile(journalPath, journal);
        }

        for (var i = journal.Entries.Count - 1; i >= 0; i--)
        {
            var entry = journal.Entries[i];
            var relative = Normalize(entry.Path);
            var destination = Resolve(rootFull, relative);
            if (entry.Existed)
            {
                var backup = Resolve(backupFull, relative);
                if (!File.Exists(backup))
                {
                    throw new SeikoException($"rollback backup is missing: {relative}", "deploy");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (Directory.Exists(destination))
                {
                    RemoveEmptyDirectories(destination);
                    if (Directory.Exists(destination))
                    {
                        throw new SeikoException(
                            $"rollback cannot restore file {relative}: a non-empty directory is in the way",
                            "deploy");
                    }
                }

                // Do not copy over an existing path: a deployed file may be a
                // hardlink to a shared store payload, and an in-place copy
                // would truncate the store inode. Replace the directory entry
                // through a fresh temporary file owned by this transaction.
                var temporary = Resolve(rootFull, restoreTemporaries[relative]);
                CreateRestoreTemporary(temporary, backup);
                File.Move(temporary, destination, overwrite: true);
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
            var path = Resolve(rootFull, normalized);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static void ValidateJournalPaths(
        string root,
        string backupDirectory,
        DeploymentJournal journal)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal.Entries)
        {
            var relative = Normalize(entry.Path);
            Resolve(root, relative);
            if (!entries.Add(relative))
            {
                throw new SeikoException(
                    $"duplicate entry in deployment transaction: {relative}",
                    "deploy");
            }
        }

        foreach (var temporary in journal.TemporaryPaths ?? [])
        {
            var relative = Normalize(temporary);
            Resolve(root, relative);
            if (!IsDeploymentTemporary(relative, entries)
                && !IsRestoreTemporary(relative, entries))
            {
                throw new SeikoException(
                    $"invalid deployment transaction temporary path: {relative}",
                    "deploy");
            }
        }

        if (journal.NewGenerationPath is { } generation)
        {
            var relative = Normalize(generation);
            Resolve(root, relative);
            var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(backupDirectory))!;
            var stateRelative = Path.GetRelativePath(root, stateDirectory).Replace('\\', '/');
            if (stateRelative == ".")
            {
                stateRelative = "";
            }

            var prefix = stateRelative.Length == 0
                ? "generations/"
                : stateRelative + "/generations/";
            var leaf = relative.StartsWith(prefix, StringComparison.Ordinal)
                ? relative[prefix.Length..]
                : "";
            if (leaf.Length == 0 || !leaf.All(char.IsAsciiDigit))
            {
                throw new SeikoException(
                    $"invalid deployment transaction generation path: {relative}",
                    "deploy");
            }
        }
    }

    private static bool IsDeploymentTemporary(string relative, HashSet<string> entries)
    {
        const string marker = ".seiko-tmp-";
        var index = relative.LastIndexOf(marker, StringComparison.Ordinal);
        if (index <= 0
            || relative.Length != index + marker.Length + 32
            || !IsLowerHex(relative.AsSpan(index + marker.Length)))
        {
            return false;
        }

        return entries.Contains(relative[..index]);
    }

    private static bool IsRestoreTemporary(string relative, HashSet<string> entries)
    {
        const string prefix = ".seiko-restore-";
        var slash = relative.LastIndexOf('/');
        var leaf = slash < 0 ? relative : relative[(slash + 1)..];
        if (!leaf.StartsWith(prefix, StringComparison.Ordinal)
            || leaf.Length != prefix.Length + 32
            || !IsLowerHex(leaf.AsSpan(prefix.Length)))
        {
            return false;
        }

        var directory = slash < 0 ? "" : relative[..(slash + 1)];
        foreach (var entry in entries)
        {
            var entrySlash = entry.LastIndexOf('/');
            var entryDirectory = entrySlash < 0 ? "" : entry[..(entrySlash + 1)];
            if (string.Equals(entryDirectory, directory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> PlanRestoreTemporaries(
        string root,
        DeploymentJournal journal)
    {
        var temporaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in journal.Entries)
        {
            if (!entry.Existed)
            {
                continue;
            }

            var relative = Normalize(entry.Path);
            if (temporaries.ContainsKey(relative))
            {
                continue;
            }

            var relativeTemporary = RestoreTemporaryName(root, relative);
            temporaries[relative] = relativeTemporary;
            journal.TemporaryPaths!.Add(relativeTemporary);
        }

        return temporaries;
    }

    // A fixed-length leaf keeps the temporary path valid when the destination
    // basename is already close to the filesystem's component limit.
    private static string RestoreTemporaryName(string root, string relative)
    {
        var slash = relative.LastIndexOf('/');
        var directory = slash < 0 ? "" : relative[..(slash + 1)];
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = directory + ".seiko-restore-" + Guid.NewGuid().ToString("N");
            var path = Resolve(root, candidate);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return candidate;
            }
        }

        throw new SeikoException(
            $"could not reserve a restore temporary for {relative}",
            "deploy");
    }

    private static void CreateRestoreTemporary(string temporary, string backup)
    {
        using var output = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var input = File.OpenRead(backup);
        input.CopyTo(output);
    }

    internal static void RemoveEmptyDirectories(string path)
    {
        var order = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var info = new DirectoryInfo(current);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            order.Add(current);
            try
            {
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var current = order[i];
            try
            {
                if (!Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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

    private static void WriteJournalFile(string journalPath, DeploymentJournal journal)
    {
        var temp = journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(
            temp,
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));
        File.Move(temp, journalPath, overwrite: true);
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
        => RelativePath.Parse(relative).Value;

    private static string Resolve(string root, string relative)
        => RootedPath.Resolve(root, RelativePath.Parse(relative));

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
