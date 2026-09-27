namespace QnocrPoc.Models;

public sealed record MessageSession(string SessionId, string SellerId, string BuyerId, string ObservedBuyer,
    string IdentityBinding, string ProductId, string? IdentityPath,
    string? BuyerDisplayName = null, DateTimeOffset CreatedAt = default, DateTimeOffset LastActivityAt = default,
    IReadOnlyList<ProductReference>? ProductReferences = null, BuyerIdentity? Identity = null);
public sealed record MessageLifecycle(DateTimeOffset CaptureTime, string Source, string Buyer, string ProductId,
    string SessionId, string MessageId, string Fingerprint, string Stage, string AdapterResult, string DispatchResult,
    string AiState, int Attempt, string? Detail = null);

/// <summary>Observed session input for an AI request; buyer ID is an explicit isolated test binding.</summary>
public sealed record SessionContextView(string SessionId, string BuyerId, string ProductId,
    IReadOnlyList<EnvelopeMessage> Messages, DateTimeOffset CapturedAt, string CaptureId, string MessageId,
    string AiRequestId, int ModelInputMessageCount, string? BuyerDisplayName = null,
    DateTimeOffset CreatedAt = default, DateTimeOffset LastActivityAt = default,
    IReadOnlyList<ProductReference>? ProductReferences = null,
    IReadOnlyList<MessageProductLink>? MessageProductLinks = null, BuyerIdentity? Identity = null);

/// <summary>Model confidence is unavailable until a calibrated score exists; never invent a probability.</summary>
public sealed record ReplyPreview(string SessionId, string BuyerId, SessionContextView Input, string Reply,
    double? Confidence, DateTimeOffset CreatedAt, string SendStatus, string CaptureId, string AiRequestId,
    string AiResponseId, string ReplyPreviewId, IntentResult? Decision = null,
    string? KnowledgeStatus = null, string? KnowledgeSource = null,
    bool KnowledgeUsedByAi = false, bool KnowledgeGuardApplied = false,
    string? ProductKnowledgeVersion = null, bool MissingProduct = false,
    string? KnowledgeCategory = null)
{
    public string? Intent => Decision?.IntentName;
    public string? RiskLevel => Decision?.RiskLevel;
}

public sealed record AgentPipelineEvent(DateTimeOffset Timestamp, string Event, string CaptureId,
    string? SessionId, string? BuyerId, string? MessageId, string? AiRequestId, string? ReplyPreviewId,
    string ProductId, int MessageCount, int? PromptLength, string? PromptLengthScope, string? Detail,
    string? AiResponseId = null, string? Intent = null, double? Confidence = null, string? RiskLevel = null,
    string? Source = null, bool? Success = null,
    string? ProductReference = null, IReadOnlyList<string>? NeedFields = null,
    IReadOnlyList<string>? MissingFields = null, IReadOnlyList<string>? ReturnedFields = null,
    string? SellerId = null, string? ProductVersion = null, bool? MissingProduct = null,
    string? KnowledgeCategory = null, string? KnowledgeId = null,
    string? KnowledgeSourceType = null, string? KnowledgeReference = null,
    string? StorageSourceType = null, string? Provider = null, bool? FallbackUsed = null,
    double? IntentLatencyMilliseconds = null);
