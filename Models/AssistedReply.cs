namespace QnocrPoc.Models;

public static class AssistedReplyStatuses
{
    public const string AiGenerated = "AIGenerated";
    public const string HumanEdited = "HumanEdited";
    public const string Approved = "Approved";
    public const string Copied = "Copied";
}

public sealed record AssistedReplyState(string ReplyPreviewId, string SessionId, string BuyerId,
    string OriginalAiReply, string? EditedReply, string Status, string ReplySha256,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt = null, string? EditedBy = null,
    DateTimeOffset? ApprovedAt = null, string? ApprovedBy = null,
    DateTimeOffset? CopiedAt = null, string? CopiedBy = null,
    string? ManualOutcome = null, DateTimeOffset? OutcomeAt = null, string? OutcomeBy = null);

public sealed record AssistedReplyView(string ReplyPreviewId, string SessionId, string BuyerId,
    string? BuyerDisplayName, string BuyerIdentityStatus, IReadOnlyList<ProductReference> ProductReferences,
    string OriginalMessage, string AiReply, string? EditedReply, string EffectiveReply,
    DateTimeOffset CreatedAt, string RiskLevel, string RiskReason, string Status, string ReplySha256,
    DateTimeOffset? EditedAt, string? EditedBy, DateTimeOffset? ApprovedAt, string? ApprovedBy,
    DateTimeOffset? CopiedAt, string? CopiedBy, string SendStatus,
    string? ManualOutcome, DateTimeOffset? OutcomeAt, string? OutcomeBy,
    IntentResult? Decision = null)
{
    public string? DecisionIntent => Decision?.IntentName;
    public string? DecisionRiskLevel => Decision?.RiskLevel;
}

public sealed record AssistantAuditEvent(DateTimeOffset Timestamp, string Event, string ReplyPreviewId,
    string CaptureId, string SessionId, string AiRequestId, string AiResponseId,
    string? OperatorId, string ReplySha256, string Detail);
