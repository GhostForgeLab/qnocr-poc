namespace QnocrPoc.Models;

/// <summary>Provider result; confidence is null when unavailable or uncalibrated.</summary>
public sealed record KnowledgeResult(bool Success, string Source, double? Confidence,
    string Content, IReadOnlyList<string> MissingFields);

public static class KnowledgeStatuses
{
    public const string Available = "Available";
    public const string Missing = "Missing";
    public const string NotRequired = "NotRequired";
}
