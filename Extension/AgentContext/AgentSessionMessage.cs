namespace QnocrPoc.Extension.AgentContext;

/// <summary>MessageId is caller-supplied or deterministically generated; Timestamp preserves input DateTime and Kind.</summary>
public sealed record AgentSessionMessage(string MessageId, DateTime Timestamp, string Content,
    string Source, string SessionId, string Role);
