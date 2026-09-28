using System.Collections.ObjectModel;
using QnocrPoc.Extension.AgentBridge;

namespace QnocrPoc.RuntimeObserver;

/// <summary>Process health only. It cannot represent a buyer message or verified identity.</summary>
public sealed class RuntimeObservedEvent
{
    public const string ObserverSource = "ExternalProcessObserver";
    public const string ObservedIdentity = "Observed";

    public RuntimeObservedEvent(string eventType, DateTimeOffset timestamp,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (metadata?.ContainsKey("State") == true)
            throw new ArgumentException("State is reserved for the bridge mapping.", nameof(metadata));
        EventType = eventType;
        Timestamp = timestamp;
        Metadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal));
        ToAgentEventEnvelope().ValidateForTransport();
    }

    public string EventType { get; }
    public DateTimeOffset Timestamp { get; }
    public string Source => ObserverSource;
    public string IdentityStatus => ObservedIdentity;
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public AgentEventEnvelope ToAgentEventEnvelope()
    {
        var metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal)
        {
            ["State"] = EventType
        };
        return new AgentEventEnvelope(AgentEventEnvelope.RuntimeObserved, "", Timestamp,
            IdentityStatus, "", "", "", Source: Source, Metadata: metadata);
    }

    public static RuntimeObservedEvent FromAgentEventEnvelope(AgentEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.ValidateForTransport();
        if (envelope.EventType != AgentEventEnvelope.RuntimeObserved)
            throw new InvalidDataException("Agent bridge event is not a runtime observation.");
        var metadata = new Dictionary<string, string>(envelope.Metadata!, StringComparer.Ordinal);
        var state = metadata["State"];
        metadata.Remove("State");
        return new RuntimeObservedEvent(state, envelope.Timestamp, metadata);
    }
}
