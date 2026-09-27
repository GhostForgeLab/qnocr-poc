using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class IntentRuntimeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var stats = new IntentRuntimeStatistics();
        var now = DateTimeOffset.UtcNow;
        stats.Observe(new IntentProviderTrace("JEV_PROVIDER_UNAVAILABLE", "JEV", null, true, now,
            Reason: "ApiKeyMissing"));
        stats.Observe(new IntentProviderTrace("INTENT_PROVIDER_RESULT", "JEV", "MATERIAL_QUERY", false,
            now, new string('A', 64), new string('1', 64), "fixture:capture", LatencyMilliseconds: 100));
        stats.Observe(new IntentProviderTrace("JEV_PROVIDER_FAILED", "JEV", null, true, now,
            Reason: "HttpRequestException", LatencyMilliseconds: 40));
        stats.Observe(new IntentProviderTrace("INTENT_PROVIDER_RESULT", "RuleBased", "OTHER", true,
            now, new string('A', 64), new string('2', 64), "fixture:capture", LatencyMilliseconds: 50));
        var snapshot = stats.Snapshot();
        check(snapshot.TotalIntentRequests == 2 && snapshot.JEVSuccess == 1 &&
            snapshot.JEVFailure == 1 && snapshot.RuleFallbackCount == 1 &&
            snapshot.AverageLatencyMilliseconds == 75 && snapshot.JevUnavailableAtStartup == 1,
            "intent runtime summary counts completed requests, JEV outcomes, fallback and average latency");
        try { stats.Observe(new IntentProviderTrace("INTENT_PROVIDER_RESULT", "JEV", "OTHER", false,
            now, LatencyMilliseconds: double.NaN)); throw new Exception("NaN latency accepted"); }
        catch (InvalidDataException) { check(stats.Snapshot().TotalIntentRequests == 2,
            "invalid latency cannot corrupt completed intent statistics"); }
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV128", "intent_review_" + Guid.NewGuid().ToString("N")));
        var state = Path.Combine(root, "state"); var run = Path.Combine(root, "run");
        Directory.CreateDirectory(run);
        Directory.CreateDirectory(Path.Combine(state, "outbox"));
        File.WriteAllText(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new { StateDirectory = state }));
        var trace = new AgentPipelineTrace(run);
        void Add(string messageId, string text, string intent)
        {
            var folder = Path.Combine(state, "outbox", messageId);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "envelope.json"), JsonSerializer.Serialize(
                new MessageEnvelope("Qianniu.OCR", "seller-test", "buyer-test", "",
                    [new EnvelopeMessage("buyer", text, new DateTime(2026, 9, 27, 12, 0, 0))])));
            trace.IntentProviderObserved(new IntentProviderTrace("INTENT_PROVIDER_RESULT", "JEV", intent,
                false, DateTimeOffset.UtcNow, new string('A', 64), messageId, "fixture:capture",
                LatencyMilliseconds: 30));
        }
        var first = new string('1', 64); var second = new string('2', 64);
        Add(first, "这款200ml能寄到13800138000吗？", "SHIPPING_QUERY");
        Add(second, "是不是TA1纯钛？", "MATERIAL_QUERY");
        var review = new IntentQualityReviewService(run);
        var pending = review.List();
        check(pending.Count == 2 && pending.Single(item => item.MessageId == first).RedactedMessage.Contains("[REDACTED_PHONE]") &&
            pending.All(item => item.HumanLabel is null),
            "human review list joins intent results to locally redacted buyer questions");
        var correct = review.Mark(first, IntentReviewLabels.IntentCorrect, "operator-test");
        var wrong = review.Mark(second, IntentReviewLabels.IntentWrong, "operator-test");
        check(correct.HumanLabel == "IntentCorrect" && wrong.HumanLabel == "IntentWrong" &&
            review.List().Count(item => item.HumanLabel is not null) == 2,
            "operator explicitly records correct and wrong intent judgments without changing classifier output");
        try { review.Mark(first, IntentReviewLabels.IntentWrong, "operator-test");
            throw new Exception("duplicate human intent review accepted"); }
        catch (InvalidOperationException) { check(true, "intent quality mark is immutable per message"); }
        try { review.Mark(new string('3', 64), IntentReviewLabels.IntentCorrect, "operator-test");
            throw new Exception("nonexistent message review accepted"); }
        catch (KeyNotFoundException) { check(true, "intent review cannot invent a missing captured message"); }
        var audit = File.ReadAllText(Path.Combine(run, "intent-review.jsonl"));
        check(audit.Contains("INTENT_HUMAN_MARKED") && !audit.Contains("13800138000") &&
            !audit.Contains("TA1纯钛"),
            "human intent audit records labels and IDs but not buyer text");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "intent quality review never creates a send request");
    }
}
