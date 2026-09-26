using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed record SessionRunState(string SessionId, string Status, DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt, DateTimeOffset? ClosedAt, int ProductReferenceCount);
public sealed record SessionRunMetrics(int Observed, int Active, int Inactive, int Closed,
    int ProductReferences, int Reopened);

/// <summary>Run-local lifecycle audit. Closing a run never erases the durable buyer/product session.</summary>
public sealed class SessionRunLifecycle(string stateRoot, string runDirectory)
{
    private readonly Dictionary<string, SessionRunState> _states = new(StringComparer.Ordinal);
    private readonly string _stateRoot = Path.GetFullPath(stateRoot);
    private readonly string _runDirectory = Path.GetFullPath(runDirectory);
    private int _reopened;

    public void Observe(MessageSession session, DateTimeOffset at)
    {
        var path = StatePath(session.SessionId);
        SessionRunState? old = null;
        if (_states.TryGetValue(session.SessionId, out var inMemory)) old = inMemory;
        else if (File.Exists(path)) old = JsonSerializer.Deserialize<SessionRunState>(File.ReadAllText(path));
        if (File.Exists(path) && old is null) throw new InvalidDataException("Unreadable session run state; refusing to overwrite.");
        var reopen = old?.Status is "Closed" or "Inactive";
        if (reopen) _reopened++;
        var next = new SessionRunState(session.SessionId, "Active", old?.FirstSeenAt ?? at,
            at, null, session.ProductReferences?.Count ?? 0);
        AtomicJson.Write(path, next);
        _states[session.SessionId] = next;
        if (old is null || reopen)
            RunLogWriter.Append(_runDirectory, "session-lifecycle.jsonl", new
            {
                At = at, Event = old is null ? "SESSION_RUN_OPENED" : "SESSION_RUN_RESUMED",
                session.SessionId, session.BuyerId, ProductReferenceCount = next.ProductReferenceCount,
                PreviousStatus = old?.Status
            });
    }

    public void SweepInactive(DateTimeOffset now, TimeSpan idle)
    {
        foreach (var state in _states.Values.Where(s => s.Status == "Active" && now - s.LastSeenAt >= idle).ToArray())
        {
            var next = state with { Status = "Inactive" };
            Save(next, now, "SESSION_RUN_INACTIVE");
        }
    }

    public void CloseAll(DateTimeOffset now)
    {
        foreach (var state in _states.Values.Where(s => s.Status != "Closed").ToArray())
        {
            var next = state with { Status = "Closed", ClosedAt = now };
            Save(next, now, "SESSION_RUN_CLOSED");
        }
    }

    public SessionRunMetrics Snapshot() => new(_states.Count,
        _states.Values.Count(s => s.Status == "Active"),
        _states.Values.Count(s => s.Status == "Inactive"),
        _states.Values.Count(s => s.Status == "Closed"),
        _states.Values.Sum(s => s.ProductReferenceCount), _reopened);

    private void Save(SessionRunState state, DateTimeOffset now, string eventName)
    {
        AtomicJson.Write(StatePath(state.SessionId), state);
        _states[state.SessionId] = state;
        RunLogWriter.Append(_runDirectory, "session-lifecycle.jsonl", new
        {
            At = now, Event = eventName, state.SessionId, state.Status,
            state.LastSeenAt, state.ProductReferenceCount
        });
    }

    private string StatePath(string sessionId) => Path.Combine(_stateRoot, "sessions", sessionId, "run-state.json");
}
