using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Agent-owned lifecycle only; no qnbot session, AI, intent, knowledge or send dependency.</summary>
public interface IAgentSessionManager
{
    /// <summary>Append one envelope to its exact observed identity and return an immutable snapshot.</summary>
    AgentSession ProcessMessage(MessageEnvelope envelope);

    /// <summary>Return the latest snapshot by local ID, applying idle expiration first.</summary>
    AgentSession? GetSession(string sessionId);

    /// <summary>Mark idle sessions Inactive; returns the number newly expired.</summary>
    int ExpireInactiveSessions();
}
