namespace QnocrPoc.Models;

public sealed record EvaluationBatch(string BatchId, DateTimeOffset StartTime, DateTimeOffset? EndTime,
    string Environment, string Operator, string StateDirectory, string RunDirectory, string Status);

public sealed record TaggedTrialQuestion(string ReplyPreviewId, string BuyerQuestion, string QuestionCategory,
    string? Reason, string ProductId, string? OperatorNote);

public sealed record TrialBatchReport(EvaluationBatch Batch, DateTimeOffset ObservedAt,
    int TotalConsultations, int BuyerMessages, int AiSuggestions, int FeedbackTotal,
    int Accepted, int Edited, int Rejected, int Unreviewed,
    double? AiAdoptionRate, double? HumanEditRate, double? RejectionRate, double? FeedbackCoverage,
    IReadOnlyDictionary<string, int> QuestionCategories, IReadOnlyDictionary<string, int> FeedbackReasons,
    IReadOnlyList<TaggedTrialQuestion> RejectedQuestions,
    IReadOnlyList<TaggedTrialQuestion> MissingProductInformationQuestions,
    int AiCalls, int? SentMessages, string SendSafetySource, int ReadErrors,
    int ClassifiedFeedback = 0, int KnowledgeGapCount = 0, int StyleIssueCount = 0,
    int RuleIssueCount = 0, int IntentIssueCount = 0, int OtherCount = 0,
    int UnclassifiedLegacyCount = 0);
