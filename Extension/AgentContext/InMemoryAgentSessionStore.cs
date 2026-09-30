namespace QnocrPoc.Extension.AgentContext;

/// <summary>Thread-safe in-memory storage; copies collections to preserve snapshot ownership.</summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void SaveSession(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        var snapshot = session with
        {
            Messages = Array.AsReadOnly(session.Messages.ToArray()),
            StateHistory = Array.AsReadOnly(session.StateHistory.ToArray())
        };
        lock (_gate) _sessions[session.SessionId] = snapshot;
    }

    public AgentSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) return _sessions.GetValueOrDefault(sessionId);
    }

    public bool DeleteSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) return _sessions.Remove(sessionId);
    }
}
