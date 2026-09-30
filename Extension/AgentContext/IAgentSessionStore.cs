namespace QnocrPoc.Extension.AgentContext;

/// <summary>
/// Storage seam for complete immutable session snapshots, including messages and transition history.
/// A manager exclusively owns a fresh/empty store. Each operation must be atomic: a failed write/delete
/// leaves the stored value unchanged. Cross-operation transactions and restart recovery are not provided.
/// </summary>
public interface IAgentSessionStore
{
    void SaveSession(AgentSession session);
    AgentSession? GetSession(string sessionId);
    bool DeleteSession(string sessionId);
}
