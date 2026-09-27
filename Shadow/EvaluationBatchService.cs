using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

/// <summary>One explicit evaluation batch per Shadow run. This only reads captures and writes local review metadata.</summary>
public sealed class EvaluationBatchService(string stateRoot)
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string BatchesRoot => Path.Combine(_root, "evaluation-batches");

    public EvaluationBatch Start(string runDirectory, string environment, string operatorId)
    {
        ValidateOperator(operatorId);
        if (environment is not ("RealQianniuShadow" or "HistoricalReview" or "IsolatedFixture"))
            throw new ArgumentException("Use RealQianniuShadow, HistoricalReview or IsolatedFixture.", nameof(environment));
        var run = Path.GetFullPath(runDirectory);
        using (var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "session.json"))))
        {
            if (!metadata.RootElement.TryGetProperty("SendEnabled", out var sendEnabled) ||
                sendEnabled.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Evaluation batches require explicit SendEnabled=false run metadata.");
        }
        // Confirms the run/state pairing before creating a batch.
        var snapshot = new AssistantRuntimeDashboard(_root, run).Snapshot();
        if (snapshot.SentMessages is > 0 || environment != "HistoricalReview" && snapshot.SentMessages is null)
            throw new InvalidDataException("Batch requires zero recorded sends; unknown historical counts are review-only.");
        Directory.CreateDirectory(BatchesRoot);
        using var gate = Lock();
        foreach (var folder in Directory.EnumerateDirectories(BatchesRoot))
        {
            var path = Path.Combine(folder, "batch.json");
            if (!File.Exists(path)) continue;
            var old = JsonSerializer.Deserialize<EvaluationBatch>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Existing batch metadata is unreadable.");
            if (string.Equals(Path.GetFullPath(old.RunDirectory), run, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This Shadow run already belongs to an evaluation batch.");
        }
        var batch = new EvaluationBatch(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, null,
            environment, operatorId, _root, run, "Open");
        AtomicJson.Write(BatchPath(batch.BatchId), batch);
        Audit(batch, "BATCH_STARTED", operatorId);
        return batch;
    }

    public EvaluationBatch Get(string batchId)
    {
        ValidateBatchId(batchId);
        var path = BatchPath(batchId);
        if (!File.Exists(path)) throw new FileNotFoundException("Evaluation batch not found.", path);
        var batch = JsonSerializer.Deserialize<EvaluationBatch>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Evaluation batch is unreadable.");
        if (batch.BatchId != batchId || !string.Equals(Path.GetFullPath(batch.StateDirectory), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Evaluation batch identity or state root differs.");
        return batch;
    }

    public EvaluationBatch End(string batchId, string operatorId)
    {
        ValidateOperator(operatorId);
        using var gate = Lock();
        var batch = Get(batchId);
        if (batch.Status != "Open" || batch.EndTime is not null) throw new InvalidOperationException("Batch is already closed.");
        if (!string.Equals(batch.Operator, operatorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the recorded batch operator can close this batch.");
        batch = batch with { EndTime = DateTimeOffset.Now, Status = "Closed" };
        AtomicJson.Write(BatchPath(batch.BatchId), batch);
        Audit(batch, "BATCH_ENDED", operatorId);
        return batch;
    }

    public AssistantFeedbackRecord RecordFeedback(string batchId, string previewId, string operatorId,
        string feedbackType, string questionCategory, string? reason, string? rejectedFinalReply = null,
        string productAssessment = "Unknown", string contextAssessment = "Unknown", string? operatorNote = null)
    {
        ValidateOperator(operatorId);
        var batch = Get(batchId);
        if (batch.Status != "Open") throw new InvalidOperationException("Closed batches cannot accept new feedback.");
        if (!string.Equals(batch.Operator, operatorId, StringComparison.Ordinal))
            throw new InvalidOperationException("Feedback operator differs from this batch operator.");
        if (questionCategory == "未分类") throw new ArgumentException("A human question category is required.", nameof(questionCategory));
        if (feedbackType is ReplyFeedbackTypes.Edited or ReplyFeedbackTypes.Rejected && reason is null)
            throw new ArgumentException("Edited/Rejected feedback needs an explicit human reason.", nameof(reason));
        var dashboard = new AssistantRuntimeDashboard(_root, batch.RunDirectory);
        if (!dashboard.Samples().Any(sample => sample.ReplyPreviewId == previewId))
            throw new InvalidDataException("Preview does not belong to this batch's Shadow run.");
        var record = new AssistantReplyService(_root).RecordFeedback(previewId, operatorId, feedbackType,
            rejectedFinalReply, productAssessment, contextAssessment, questionCategory, reason, batch.BatchId, operatorNote);
        Audit(batch, "BATCH_FEEDBACK_RECORDED", operatorId, previewId, feedbackType);
        return record;
    }

    public TrialBatchReport Report(string batchId)
    {
        var batch = Get(batchId);
        var dashboard = new AssistantRuntimeDashboard(_root, batch.RunDirectory);
        var stats = dashboard.Snapshot();
        var records = dashboard.FeedbackRecords();
        var classifications = records.Select(r => r.Classification).Where(c => c is not null).ToArray();
        var accepted = records.Count(r => r.Feedback.FeedbackType == ReplyFeedbackTypes.Accepted);
        var edited = records.Count(r => r.Feedback.FeedbackType == ReplyFeedbackTypes.Edited);
        var rejected = records.Count(r => r.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected);
        var total = accepted + edited + rejected;
        var categories = records.GroupBy(r => r.Feedback.QuestionCategory, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var reasons = records.Where(r => r.Feedback.Reason is not null)
            .GroupBy(r => r.Feedback.Reason!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        static TaggedTrialQuestion Tagged(AssistantFeedbackRecord record) => new(record.Feedback.ReplyPreviewId,
            record.Sample.BuyerQuestion, record.Feedback.QuestionCategory, record.Feedback.Reason,
            record.Feedback.ProductId, record.Feedback.OperatorNote);
        return new TrialBatchReport(batch, DateTimeOffset.Now, stats.SessionCount, stats.ProcessedMessages,
            stats.AiSuggestions, total, accepted, edited, rejected, Math.Max(0, stats.AiSuggestions - total),
            Rate(accepted, total), Rate(edited, total), Rate(rejected, total), Rate(total, stats.AiSuggestions),
            categories, reasons,
            records.Where(r => r.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected).Select(Tagged).ToArray(),
            records.Where(r => r.Feedback.Reason == "缺少商品信息").Select(Tagged).ToArray(),
            stats.AiCalls, stats.SentMessages, stats.SendSafetySource, stats.ReadErrors,
            classifications.Length,
            classifications.Count(c => c!.FeedbackType == FeedbackClassificationTypes.KnowledgeGap),
            classifications.Count(c => c!.FeedbackType == FeedbackClassificationTypes.StyleIssue),
            classifications.Count(c => c!.FeedbackType == FeedbackClassificationTypes.RuleIssue),
            classifications.Count(c => c!.FeedbackType == FeedbackClassificationTypes.IntentIssue),
            classifications.Count(c => c!.FeedbackType == FeedbackClassificationTypes.Other),
            records.Count - classifications.Length);
    }

    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
    private static void ValidateOperator(string operatorId)
    {
        if (string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 128)
            throw new ArgumentException("A nonempty operator label of at most 128 characters is required.", nameof(operatorId));
    }
    private static void ValidateBatchId(string batchId)
    {
        if (batchId.Length != 32 || !batchId.All(Uri.IsHexDigit))
            throw new ArgumentException("BatchId must be 32 hexadecimal characters.", nameof(batchId));
    }
    private string BatchPath(string batchId) => Path.Combine(BatchesRoot, batchId, "batch.json");
    private FileStream Lock()
    {
        Directory.CreateDirectory(BatchesRoot);
        return new FileStream(Path.Combine(BatchesRoot, "batches.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Audit(EvaluationBatch batch, string kind, string operatorId, string? previewId = null, string? feedbackType = null) =>
        RunLogWriter.Append(BatchesRoot, "batch-events.jsonl", new
        {
            At = DateTimeOffset.Now, Event = kind, batch.BatchId, Operator = operatorId,
            batch.RunDirectory, PreviewId = previewId, FeedbackType = feedbackType, SendEnabled = false
        });
}
