using QnocrPoc.RuntimeObserver;

namespace QnocrPoc.Extension.AgentBridge;

/// <summary>Agent-side, observation-only entry point; it has no message, AI or send path.</summary>
public sealed class AgentRuntimeObservationReceiver(IAgentBridge bridge)
{
    private readonly IAgentBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public async Task<RuntimeObservedEvent> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await _bridge.ReceiveAsync(cancellationToken);
        return RuntimeObservedEvent.FromAgentEventEnvelope(envelope);
    }
}
