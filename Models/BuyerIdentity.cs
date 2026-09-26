namespace QnocrPoc.Models;

public static class BuyerIdentityStatuses
{
    public const string Unknown = "Unknown";
    public const string Observed = "Observed";
    public const string Mapped = "Mapped";
    public const string Verified = "Verified";
}

/// <summary>Identity assurance is independent of OCR text confidence and product references.</summary>
public sealed record BuyerIdentity(string BuyerId, string Source, double? Confidence,
    DateTimeOffset CapturedAt, string SessionId, string Status,
    string? BuyerDisplayName = null, string? PlatformConversationId = null,
    string? EvidenceId = null, bool IsTest = false, DateTimeOffset? VerifiedAt = null);

public sealed record BuyerSendDecision(bool Allowed, string Code, string Detail);
