namespace QnocrPoc.Models;

/// <summary>Comparison evidence only; neither result is automatically selected for the Agent.</summary>
public sealed record IntentComparison(string Id, string Message, string? ExpectedIntent,
    string RuleIntent, string? JevIntent, bool? Match, string Difference,
    double? JevConfidence, string? JevModel, DateTimeOffset Timestamp,
    string? JevError = null);

public sealed record IntentSample(string Id, string Message, string ExpectedIntent);
