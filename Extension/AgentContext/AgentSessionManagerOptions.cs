namespace QnocrPoc.Extension.AgentContext;

/// <summary>Count limits reject new messages or evict only non-Active sessions.</summary>
public sealed record AgentSessionManagerOptions
{
    public int MaxMessagesPerSession { get; init; } = 256;
    public int MaxSessions { get; init; } = 1024;
    // Retention starts at the idle deadline, even if a sweep runs late.
    public TimeSpan ArchiveAfterIdleTimeout { get; init; } = TimeSpan.FromHours(24);
    // Opt-in Agent-owned scheduler. The owner must dispose the manager.
    public TimeSpan? LifecycleSweepInterval { get; init; }
}

public sealed class AgentSessionCapacityException(string message) : InvalidOperationException(message);
