namespace QnocrPoc.Models;

/// <summary>Explicit optional AI-input sidecar; only bounded test facts may populate FactFields.</summary>
public sealed record KnowledgeContextView(string Status, string ProductId, string Source,
    IReadOnlyDictionary<string, string> FactFields, IReadOnlyList<string> MissingFields,
    bool IsTestData, DateTimeOffset? UpdatedAt);
