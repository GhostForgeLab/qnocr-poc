namespace QnocrPoc.Models;

/// <summary>Store-scoped local test data; not a verified platform catalog record.</summary>
public sealed record ProductKnowledgeRecord(string ProductId, Dictionary<string, string> Fields,
    string Source, DateTimeOffset UpdatedAt, bool IsTestData,
    string SellerId = "", string Version = "");
