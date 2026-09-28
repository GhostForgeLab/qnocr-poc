using System.Diagnostics;

namespace QnocrPoc.RuntimeObserver;

/// <summary>Reads only process presence and PID; it never opens a process or reads its memory.</summary>
public sealed class ExternalProcessObserver : IRuntimeObserver
{
    private readonly string _processName;
    private readonly Func<string, IReadOnlyCollection<int>> _readProcessIds;
    private HashSet<int>? _previous;
    private bool _stopped;

    public ExternalProcessObserver(string processName,
        Func<string, IReadOnlyCollection<int>>? readProcessIds = null)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName.Length > 128 ||
            processName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("A process name without a path is required.", nameof(processName));
        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A process name without a path is required.", nameof(processName));
        _processName = normalized;
        _readProcessIds = readProcessIds ?? GetProcessIds;
    }

    public IReadOnlyList<RuntimeObservedEvent> Poll()
    {
        if (_stopped) throw new InvalidOperationException("Observer has stopped.");
        var current = _readProcessIds(_processName).Where(id => id > 0).ToHashSet();
        var events = new List<RuntimeObservedEvent>();
        if (_previous is null)
        {
            events.Add(Create("ObserverStarted"));
            if (current.Count == 0) events.Add(Create("TargetAbsent", null));
            else events.AddRange(current.Order().Select(id => Create("TargetRunning", id)));
        }
        else
        {
            events.AddRange(current.Except(_previous).Order().Select(id => Create("TargetStarted", id)));
            events.AddRange(_previous.Except(current).Order().Select(id => Create("TargetExited", id)));
        }
        _previous = current;
        return events;
    }

    public RuntimeObservedEvent Stop()
    {
        if (_stopped) throw new InvalidOperationException("Observer has stopped.");
        _stopped = true;
        return Create("ObserverStopped");
    }

    private RuntimeObservedEvent Create(string state, int? pid = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.StartsWith("Target", StringComparison.Ordinal))
        {
            metadata["ProcessName"] = _processName;
            if (pid is not null) metadata["ProcessId"] = pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new RuntimeObservedEvent(state, DateTimeOffset.UtcNow, metadata);
    }

    private static IReadOnlyCollection<int> GetProcessIds(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Select(process => process.Id).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
