using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Agent-owned resolution; optional source/fallback keys travel outside the v1 envelope.</summary>
public interface IAgentMessageIdentityProvider
{
    string GetMessageId(SessionIdentity identity, EnvelopeMessage message,
        string? sourceMessageId = null, string? fallbackIdentity = null);
}
