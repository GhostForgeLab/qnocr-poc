namespace QnocrPoc.Extension.AgentContext;

/// <summary>Immutable lifecycle snapshot; activity times are UTC reception times, not message times.</summary>
public sealed record AgentSession(string SessionId, DateTimeOffset CreatedTime,
    DateTimeOffset LastActiveTime, IReadOnlyList<AgentSessionMessage> Messages,
    SessionIdentity Identity, AgentSessionStatus Status)
{
    public IReadOnlyList<AgentSessionStateChange> StateHistory { get; init; } =
        Array.AsReadOnly(Array.Empty<AgentSessionStateChange>());

    // Reserved only: no product binding, intent classification or history borrowing.
    public string? CurrentProductContext { get; } = null;
    public IReadOnlyList<string> IntentHistory { get; } = Array.AsReadOnly(Array.Empty<string>());
}
