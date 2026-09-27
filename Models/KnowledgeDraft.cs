using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeDraftStatus>))]
public enum KnowledgeDraftStatus { Draft, Reviewing, Approved, Rejected, Conflict, Published }

/// <summary>One explicit, bounded test-product input. Its values are candidate facts, never verified facts.</summary>
public sealed record ProductKnowledgeAuthoringInput(string SellerId, string ProductId, string Name,
    string Version, KnowledgeSource Source, bool IsTestData,
    IReadOnlyList<string>? Capacity = null, string? Material = null,
    IReadOnlyList<ProductFeature>? Features = null);

/// <summary>Authored profile and links to individually reviewed knowledge items.</summary>
public sealed record KnowledgeDraft(string DraftId, string SellerId, string ProductId,
    KnowledgeSource Source, DateTimeOffset CreatedAt, KnowledgeDraftStatus Status,
    ProductKnowledgeProfile Content, IReadOnlyList<string> KnowledgeItemIds,
    IReadOnlyList<string> ConflictingFields, IReadOnlyList<string> SkippedPublishedFields,
    bool AiUseAllowed = false);
