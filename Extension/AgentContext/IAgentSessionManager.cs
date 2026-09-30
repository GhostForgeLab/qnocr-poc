using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Agent-owned lifecycle only; no qnbot session, AI, intent, knowledge or send dependency.</summary>
public interface IAgentSessionManager : IDisposable
{
    /// <summary>Append with generated local IDs; retries requiring dedupe must use the stable-ID overload.</summary>
    AgentSession ProcessMessage(MessageEnvelope envelope);

    /// <summary>
    /// Supply one stable, nonblank ID per envelope message. IDs are exact and Session-scoped;
    /// duplicates keep their first content and do not refresh activity when the whole batch is duplicate.
    /// </summary>
    AgentSession ProcessMessage(MessageEnvelope envelope, IReadOnlyList<string> messageIds);

    /// <summary>Return the latest retained snapshot by local ID, applying lifecycle deadlines first.</summary>
    AgentSession? GetSession(string sessionId);

    /// <summary>Mark idle sessions Inactive; returns the number newly expired.</summary>
    int ExpireInactiveSessions();

    /// <summary>Apply idle and archive deadlines; returns the number of state transitions.</summary>
    int SweepLifecycle();
}
