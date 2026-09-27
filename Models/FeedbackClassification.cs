namespace QnocrPoc.Models;

public static class FeedbackClassificationTypes
{
    public const string KnowledgeGap = "KnowledgeGap";
    public const string StyleIssue = "StyleIssue";
    public const string RuleIssue = "RuleIssue";
    public const string IntentIssue = "IntentIssue";
    public const string Other = "Other";
}

public static class FeedbackTargets
{
    public const string Knowledge = "Knowledge";
    public const string Prompt = "Prompt";
    public const string Strategy = "Strategy";
    public const string Decision = "Decision";
    public const string None = "None";
}

/// <summary>Routing recommendation only; Confidence is null because rules have no calibrated probability.</summary>
public sealed record FeedbackClassification(string ReplyPreviewId, string FeedbackType,
    double? Confidence, string Reason, string SuggestedTarget,
    DateTimeOffset ClassifiedAt, string RuleVersion = "V1.18");
