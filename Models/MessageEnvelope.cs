namespace QnocrPoc.Models;

/// <summary>Project-owned v1 event contract; no OCR, image, UIA, or qnbot types.</summary>
public sealed record MessageEnvelope(string Source, string SellerId, string BuyerId, string ProductId,
    IReadOnlyList<EnvelopeMessage> Messages)
{
    public int SchemaVersion { get; init; } = 1;
    public string ProductBinding { get; init; } = "None";
}

/// <summary>Timestamp keeps the original displayed time and DateTime.Kind; the adapter does not infer a timezone.</summary>
public sealed record EnvelopeMessage(string Role, string Text, DateTime Timestamp);
