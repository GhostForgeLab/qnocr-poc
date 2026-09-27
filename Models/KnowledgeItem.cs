using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeStatus>))]
public enum KnowledgeStatus { Draft, Reviewing, Approved, Rejected, Published }

[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeSourceType>))]
public enum KnowledgeSourceType { ProductPage, ManualInput, ImageSpec, CustomerFeedback }

public sealed record KnowledgeSource(KnowledgeSourceType SourceType, string SourceName,
    string Reference, DateTimeOffset CapturedAt);

public sealed record KnowledgeVersion(string Version, DateTimeOffset CreatedAt,
    string? ApprovedBy, KnowledgeStatus Status);

/// <summary>Explicit human-reviewed, store-scoped test fact. Only Published+AiUseAllowed may reach a provider.</summary>
public sealed record KnowledgeItem(string KnowledgeId, string SellerId, string ProductId,
    string Category, string Key, string Value, KnowledgeSource Source,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, KnowledgeStatus Status,
    KnowledgeVersion Version, bool AiUseAllowed = false,
    string? ReviewedBy = null, DateTimeOffset? ReviewedAt = null,
    string? RejectionReason = null, bool IsTestData = true);
