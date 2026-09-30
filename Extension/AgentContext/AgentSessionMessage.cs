namespace QnocrPoc.Extension.AgentContext;

/// <summary>MessageId is caller-supplied or Agent-local; Timestamp preserves the input DateTime and Kind.</summary>
public sealed record AgentSessionMessage(string MessageId, DateTime Timestamp, string Content,
    string Source, string SessionId, string Role);
