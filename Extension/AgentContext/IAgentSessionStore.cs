namespace QnocrPoc.Extension.AgentContext;

/// <summary>
/// Storage seam for complete immutable session snapshots, including messages and transition history.
/// One manager exclusively owns the store. Each operation must be atomic: a failed write/delete
/// leaves the stored value unchanged. LoadSessions returns a consistent, complete startup snapshot.
/// </summary>
public interface IAgentSessionStore
{
    void SaveSession(AgentSession session);
    AgentSession? GetSession(string sessionId);
    bool DeleteSession(string sessionId);
    IReadOnlyList<AgentSession> LoadSessions();
}
