namespace QnocrPoc.Models;

/// <summary>One local test product only; this is not a verified catalog record.</summary>
public sealed record ProductKnowledgeRecord(string ProductId, Dictionary<string, string> Fields,
    string Source, DateTimeOffset UpdatedAt, bool IsTestData);
