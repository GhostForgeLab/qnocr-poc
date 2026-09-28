namespace QnocrPoc.Extension.AgentBridge;

/// <summary>
/// Versioned transport event. This is an observation, not a platform-verified identity
/// or a MessageEnvelope accepted for AI processing.
/// </summary>
public sealed record AgentEventEnvelope(
    string EventType,
    string SessionId,
    DateTimeOffset Timestamp,
    string IdentityStatus,
    string BuyerId,
    string Content,
    string ProductId,
    int SchemaVersion = 1,
    string? Source = null,
    Dictionary<string, string>? Metadata = null)
{
    public const string BuyerMessageObserved = "BuyerMessageObserved";
    public const string RuntimeObserved = "RuntimeObserved";

    public void ValidateForTransport()
    {
        if (SchemaVersion != 1 || IdentityStatus != "Observed" || Timestamp == default)
            throw new InvalidDataException("Invalid or untrusted Agent bridge event.");

        if (EventType == RuntimeObserved)
        {
            if (SessionId != "" || BuyerId != "" || Content != "" || ProductId != "" ||
                Source != "ExternalProcessObserver" || Metadata is null || Metadata.Count > 3 ||
                !Metadata.TryGetValue("State", out var state) ||
                state is not ("ObserverStarted" or "ObserverStopped" or "TargetRunning" or
                    "TargetAbsent" or "TargetStarted" or "TargetExited") ||
                Metadata.Any(x => x.Key is not ("State" or "ProcessName" or "ProcessId") ||
                    x.Value is null || x.Value.Length > 128) ||
                (state.StartsWith("Target", StringComparison.Ordinal) &&
                    (!Metadata.TryGetValue("ProcessName", out var name) || string.IsNullOrWhiteSpace(name) ||
                     name.IndexOfAny(['/', '\\', ':']) >= 0)) ||
                (state is "ObserverStarted" or "ObserverStopped" && Metadata.Count != 1) ||
                (state is "TargetRunning" or "TargetStarted" or "TargetExited" &&
                    !Metadata.ContainsKey("ProcessId")) ||
                (state == "TargetAbsent" && Metadata.ContainsKey("ProcessId")) ||
                (Metadata.TryGetValue("ProcessId", out var pid) &&
                    (!int.TryParse(pid, out var parsedPid) || parsedPid <= 0)))
                throw new InvalidDataException("Invalid runtime observation bridge event.");
            return;
        }

        if (EventType != BuyerMessageObserved || Source is not null || Metadata is not null ||
            string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 128 ||
            string.IsNullOrWhiteSpace(BuyerId) || BuyerId.Length > 128 ||
            string.IsNullOrWhiteSpace(Content) || Content.Length > 4000 ||
            ProductId is null || ProductId.Length > 128)
            throw new InvalidDataException("Invalid or untrusted Agent bridge event.");
    }
}
