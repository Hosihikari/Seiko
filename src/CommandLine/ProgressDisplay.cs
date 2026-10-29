using ShellProgressBar;

namespace Seiko.CommandLine;

internal sealed class ProgressDisplay : IProgress<SyncProgress>, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ChildProgressBar> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _downloaded = new(StringComparer.Ordinal);
    private ProgressBar? _parent;
    private long _totalBytes;
    private int _seiTotal;
    private int _seiCompleted;
    private bool _disposed;

    public static bool IsSupported()
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        return !string.Equals(
            Environment.GetEnvironmentVariable("TERM"),
            "dumb",
            StringComparison.OrdinalIgnoreCase);
    }

    public void Report(SyncProgress value)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (value.Phase)
            {
                case SyncPhase.Resolved:
                    Start(value);
                    break;
                case SyncPhase.Downloading:
                case SyncPhase.Completed:
                    Update(value);
                    break;
                case SyncPhase.Deploying:
                    _parent?.Tick(_parent.MaxTicks, "deploying");
                    break;
            }
        }
    }

    private void Start(SyncProgress value)
    {
        _totalBytes = value.Total;
        _seiTotal = value.SeiTotal;
        if (_seiTotal == 0)
        {
            return;
        }

        var ticks = _totalBytes > 0 ? 1000 : Math.Max(1, _seiTotal);
        _parent = new ProgressBar(
            ticks,
            $"downloading {_seiTotal} sei",
            new ProgressBarOptions
            {
                ForegroundColor = ConsoleColor.Green,
                BackgroundColor = ConsoleColor.DarkGray,
                DisplayTimeInRealTime = true,
            });
    }

    private void Update(SyncProgress value)
    {
        if (_parent is null || value.Name is null)
        {
            return;
        }

        if (value.Phase == SyncPhase.Downloading)
        {
            _downloaded[value.Name] = value.Downloaded;
            if (!_children.TryGetValue(value.Name, out var child))
            {
                child = _parent.Spawn(
                    1000,
                    value.Name,
                    new ProgressBarOptions
                    {
                        ForegroundColor = ConsoleColor.Cyan,
                        BackgroundColor = ConsoleColor.DarkGray,
                        CollapseWhenFinished = true,
                        DisplayTimeInRealTime = true,
                    });
                _children[value.Name] = child;
            }

            var tick = value.Total > 0
                ? (int)Math.Min(1000, value.Downloaded * 1000 / value.Total)
                : 0;
            child.Tick(tick, $"{FormatBytes(value.Downloaded)} / {FormatBytes(value.Total)}");
        }
        else
        {
            _downloaded[value.Name] = value.Total;
            if (_children.Remove(value.Name, out var child))
            {
                child.Tick(1000, "done");
                child.Dispose();
            }
        }

        _seiCompleted = value.SeiCompleted;
        var parentTick = _totalBytes > 0
            ? (int)Math.Min(1000, _downloaded.Values.Sum() * 1000 / _totalBytes)
            : Math.Min(_seiTotal, _seiCompleted);
        _parent.Tick(parentTick, $"{_seiCompleted}/{_seiTotal} sei");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var child in _children.Values)
            {
                child.Dispose();
            }

            _children.Clear();
            _parent?.Dispose();
            _parent = null;
        }
    }
}
