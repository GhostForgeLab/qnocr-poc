namespace QnocrPoc.Models;

/// <summary>Provider result; confidence is null when unavailable or uncalibrated.</summary>
public sealed record KnowledgeResult(bool Success, string Source, double? Confidence,
    string Content, IReadOnlyList<string> MissingFields,
    IReadOnlyDictionary<string, string>? ReturnedFields = null, DateTimeOffset? UpdatedAt = null,
    bool MissingProduct = false, string? ProductVersion = null, string? SellerId = null,
    string? KnowledgeCategory = null, KnowledgeSource? ItemSource = null,
    string? KnowledgeId = null,
    KnowledgeStorageSourceType? StorageSourceType = null);

public static class KnowledgeStatuses
{
    public const string Available = "Available";
    public const string Missing = "Missing";
    public const string NotRequired = "NotRequired";
    public const string MissingProductContext = "MissingProductContext";
}
