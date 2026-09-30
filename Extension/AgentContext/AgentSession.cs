namespace QnocrPoc.Extension.AgentContext;

public enum AgentSessionStatus
{
    Active,
    Inactive
}

/// <summary>Immutable lifecycle snapshot; activity times are UTC reception times, not message times.</summary>
public sealed record AgentSession(string SessionId, DateTimeOffset CreatedTime,
    DateTimeOffset LastActiveTime, IReadOnlyList<AgentSessionMessage> Messages,
    SessionIdentity Identity, AgentSessionStatus Status)
{
    // Reserved only: no product binding, intent classification or history borrowing.
    public string? CurrentProductContext { get; } = null;
    public IReadOnlyList<string> IntentHistory { get; } = Array.AsReadOnly(Array.Empty<string>());
}
