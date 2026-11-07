namespace QnocrPoc.Models;

public sealed class BuyerMessage
{
    public string Role { get; init; } = "buyer";
    public required string Text { get; init; }
    public required DateTime Timestamp { get; init; }
    public string Source { get; init; } = "Qianniu.PaddleOCR";
    public string ProductId { get; init; } = string.Empty;
    public string ContentKind { get; init; } = "Text";
    public string VisualFingerprint { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? PreviewPng { get; init; }
}

public sealed record BuyerMessageEvidence(int MessageIndex, OcrBounds BubbleBounds,
    IReadOnlyList<int> BodyLineNumbers, IReadOnlyList<int> HeaderLineNumbers, double MinimumConfidence);
public sealed record MessageReviewItem(string Text, string Reason, IReadOnlyList<int> LineNumbers, OcrBounds Bounds);
public sealed record BuyerMessageBatch(string ImagePath, string ImageSha256, IReadOnlyList<BuyerMessage> Messages,
    IReadOnlyList<BuyerMessageEvidence> Evidence, IReadOnlyList<MessageReviewItem> ReviewItems,
    string ProductBinding, string TimestampMeaning);
