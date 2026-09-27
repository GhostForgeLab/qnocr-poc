namespace QnocrPoc.Models;

public static class ReplyFeedbackTypes
{
    public const string Accepted = "Accepted";
    public const string Edited = "Edited";
    public const string Rejected = "Rejected";
}

/// <summary>Explicit human assessment. Text is locally redacted before persistence; it is never AI training input.</summary>
public sealed record ReplyFeedback(string ReplyPreviewId, string SessionId, string OriginalAiReply,
    string FinalHumanReply, string FeedbackType, DateTimeOffset RecordedAt, string OperatorId,
    string BuyerQuestion, string ProductId, int ContextMessageCount,
    string ProductReferenceAssessment = "Unknown", string ContextAssessment = "Unknown",
    string QuestionCategory = "未分类", string? Reason = null, string? BatchId = null,
    string? OperatorNote = null, string? DecisionIntent = null);

/// <summary>Minimal consultation evidence, created only after explicit feedback.</summary>
public sealed record ConversationSample(string ReplyPreviewId, string SessionId, string CaptureId,
    string BuyerQuestion, string AiSuggestion, string FinalHumanReply, string FeedbackType,
    string ProductId, IReadOnlyList<string> ObservedProductReferences, int ContextMessageCount,
    DateTimeOffset CapturedAt, DateTimeOffset FeedbackAt,
    string QuestionCategory = "未分类", string? Reason = null, string? BatchId = null,
    string? OperatorNote = null, string? DecisionIntent = null);

public sealed record AssistantFeedbackRecord(ReplyFeedback Feedback, ConversationSample Sample,
    FeedbackClassification? Classification = null);

public sealed record SessionObservation(string SessionId, int PreviewCount, int DistinctBuyerQuestions,
    int FeedbackCount, int ContextContinuousPairs, int ContextUnprovenPairs,
    IReadOnlyList<string> ProductIds, DateTimeOffset FirstPreviewAt, DateTimeOffset LastPreviewAt);

public sealed record AssistantRuntimeStats(DateTimeOffset ObservedAt, string StateDirectory, string RunDirectory,
    DateTimeOffset StartedAt, double RuntimeSeconds, string RunStatus, int Frames,
    int ProcessedMessages, int SessionCount, int AiSuggestions, int HumanEditActions,
    int DistinctEditedPreviews, int CopyActions, int AiNotAdopted,
    int FeedbackTotal, int FeedbackAccepted, int FeedbackEdited, int FeedbackRejected,
    double? AcceptedRate, double? EditedRate, double? RejectedRate,
    int SessionsWithMultiplePreviews, int ContextContinuousPairs, int ContextUnprovenPairs,
    int AiCalls, int? SentMessages, string SendSafetySource, int ReadErrors,
    int SuggestionsWithCurrentProductId, int SuggestionsWithoutCurrentProductId,
    int UnverifiedProductAssociations, int ProductReferencesMarkedCorrect,
    int ProductReferencesMarkedIncorrect, int ProductReferencesUnreviewed,
    int ContextMarkedContinued, int ContextMarkedLost, int ContextUnreviewed,
    double? AverageGenerationToEditSeconds,
    int ClassifiedFeedback = 0, int KnowledgeGapCount = 0, int StyleIssueCount = 0,
    int RuleIssueCount = 0, int IntentIssueCount = 0, int OtherCount = 0,
    int UnclassifiedLegacyCount = 0);
