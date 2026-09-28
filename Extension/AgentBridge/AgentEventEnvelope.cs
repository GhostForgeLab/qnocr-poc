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
    int SchemaVersion = 1)
{
    public const string BuyerMessageObserved = "BuyerMessageObserved";

    public void ValidateForTransport()
    {
        if (SchemaVersion != 1 || EventType != BuyerMessageObserved || IdentityStatus != "Observed" ||
            string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 128 ||
            string.IsNullOrWhiteSpace(BuyerId) || BuyerId.Length > 128 ||
            string.IsNullOrWhiteSpace(Content) || Content.Length > 4000 ||
            ProductId is null || ProductId.Length > 128 || Timestamp == default)
            throw new InvalidDataException("Invalid or untrusted Agent bridge event.");
    }
}
