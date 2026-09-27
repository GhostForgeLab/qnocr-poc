using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Decision;

/// <summary>Local human judgment only; never changes model, rules, decisions or training data.</summary>
public sealed class IntentQualityReviewService(string runDirectory)
{
    private readonly string _run = Path.GetFullPath(runDirectory);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string FeedbackRoot => Path.Combine(_run, "intent-feedback");

    public IReadOnlyList<IntentReviewCandidate> List()
    {
        var summaryPath = Path.Combine(_run, "summary.json");
        using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var state = summary.RootElement.GetProperty("StateDirectory").GetString()
            ?? throw new InvalidDataException("Shadow state directory is missing.");
        var rows = new List<IntentReviewCandidate>();
        foreach (var tracePath in TracePaths())
        foreach (var line in File.ReadLines(tracePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<AgentPipelineEvent>(line, Json);
            if (record?.Event != "INTENT_PROVIDER_RESULT" || record.MessageId is null ||
                record.SessionId is null || record.Provider is null || record.Intent is null)
                continue;
            if (record.MessageId.Length != 64 || !record.MessageId.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid intent message ID in trace.");
            var envelopePath = Path.Combine(state, "outbox", record.MessageId, "envelope.json");
            var question = "[MESSAGE_UNAVAILABLE]";
            if (File.Exists(envelopePath))
            {
                var envelope = JsonSerializer.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath), Json);
                question = AssistantSampleRedactor.Redact(envelope?.Messages.LastOrDefault(m => m.Role == "buyer")?.Text ?? question);
            }
            var feedbackPath = Path.Combine(FeedbackRoot, record.MessageId + ".json");
            var label = File.Exists(feedbackPath)
                ? JsonSerializer.Deserialize<IntentHumanReview>(File.ReadAllText(feedbackPath), Json)?.HumanLabel : null;
            rows.Add(new IntentReviewCandidate(record.MessageId, record.SessionId,
                record.Provider, record.Intent, record.FallbackUsed == true, question, label));
        }
        return rows.GroupBy(row => row.MessageId, StringComparer.Ordinal)
            .Select(group => group.Last()).ToArray();
    }

    public IntentHumanReview Mark(string messageId, string label, string operatorId)
    {
        if (messageId.Length != 64 || !messageId.All(Uri.IsHexDigit) ||
            label is not (IntentReviewLabels.IntentCorrect or IntentReviewLabels.IntentWrong) ||
            string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 128)
            throw new ArgumentException("Use a valid message ID, IntentCorrect/IntentWrong and operator.");
        var candidate = List().SingleOrDefault(item => item.MessageId == messageId)
            ?? throw new KeyNotFoundException("Intent result not found in this Shadow run.");
        Directory.CreateDirectory(FeedbackRoot);
        var path = Path.Combine(FeedbackRoot, messageId + ".json");
        if (File.Exists(path)) throw new InvalidOperationException("Intent judgment already recorded.");
        var review = new IntentHumanReview(candidate.MessageId, candidate.SessionId,
            candidate.Provider, candidate.Intent, label, operatorId, DateTimeOffset.UtcNow);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, review);
        RunLogWriter.Append(_run, "intent-review.jsonl", new
        {
            Event = "INTENT_HUMAN_MARKED", review.MessageId, review.SessionId,
            review.Provider, review.Intent, review.HumanLabel, review.Operator,
            Timestamp = review.ReviewedAt
        });
        return review;
    }

    private IEnumerable<string> TracePaths()
    {
        var archive = Path.Combine(_run, "log-archive");
        if (Directory.Exists(archive))
            foreach (var path in Directory.EnumerateFiles(archive, "agent-pipeline.*.jsonl").OrderBy(path => path))
                yield return path;
        var active = Path.Combine(_run, "agent-pipeline.jsonl");
        if (File.Exists(active)) yield return active;
    }
}
