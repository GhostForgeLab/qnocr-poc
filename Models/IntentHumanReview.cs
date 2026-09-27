namespace QnocrPoc.Models;

public static class IntentReviewLabels
{
    public const string IntentCorrect = "IntentCorrect";
    public const string IntentWrong = "IntentWrong";
}

public sealed record IntentReviewCandidate(string MessageId, string SessionId, string Provider,
    string Intent, bool FallbackUsed, string RedactedMessage, string? HumanLabel);

public sealed record IntentHumanReview(string MessageId, string SessionId, string Provider,
    string Intent, string HumanLabel, string Operator, DateTimeOffset ReviewedAt);
