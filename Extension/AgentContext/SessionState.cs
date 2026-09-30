namespace QnocrPoc.Extension.AgentContext;

public enum AgentSessionStatus
{
    Active,
    Inactive,
    Archived
}

/// <summary>UTC time at which the manager observed a transition; null From marks creation.</summary>
public sealed record AgentSessionStateChange(AgentSessionStatus? From, AgentSessionStatus To,
    DateTimeOffset ChangedTime, string Reason);
