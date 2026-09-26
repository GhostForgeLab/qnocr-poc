namespace QnocrPoc.Models;

/// <summary>Uncalibrated rule metadata only. NeedKnowledge does not execute a lookup.</summary>
public sealed record IntentResult(string IntentName, double? Confidence, bool NeedProductContext,
    bool NeedKnowledge, bool NeedHumanReview, string RiskLevel,
    string RuleVersion = "V1.15", string? MatchedRule = null);
