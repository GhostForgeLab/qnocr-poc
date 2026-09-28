namespace QnocrPoc.Extension.AgentBridge;

/// <summary>Consumer boundary; a later transport can replace the pipe client.</summary>
public interface IAgentBridge
{
    Task<AgentEventEnvelope> ReceiveAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentBridgeLogEntry(DateTimeOffset At, string Stage, string EventType, string SessionId);
