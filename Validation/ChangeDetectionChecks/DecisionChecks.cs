using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class DecisionChecks
{
    private sealed class ThrowingClassifier : IIntentClassifier
    {
        public IntentResult Classify(SessionContextView context) => throw new InvalidOperationException("Injected classifier failure");
    }
    private sealed class SelectiveClassifier(string failingMessageId) : IIntentClassifier
    {
        private readonly RuleBasedIntentClassifier _fallback = new();
        public IntentResult Classify(SessionContextView context) => context.MessageId == failingMessageId
            ? throw new InvalidOperationException("Injected one-message decision failure") : _fallback.Classify(context);
    }    private sealed class InvalidClassifier : IIntentClassifier
    {
        public IntentResult Classify(SessionContextView context) => new("UNKNOWN", 1.5, false, false, false, "Extreme");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var sid = new string('A', 64); var mid = new string('B', 64); var request = new string('C', 64);
        SessionContextView Context(string question) => new(sid, "buyer-test", "",
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 26, 12, 0, 0))],
            DateTimeOffset.Now, "decision-fixture:frame_000001", mid, request, 1);
        IIntentClassifier classifier = new RuleBasedIntentClassifier();
        var spec = classifier.Classify(Context("这款有200ml规格吗？"));
        check(spec.IntentName == "PRODUCT_SPEC_QUERY" && spec.NeedProductContext && spec.NeedKnowledge &&
            spec.NeedHumanReview && spec.RiskLevel == "Medium" && spec.Confidence is null,
            "product specification question yields product-context and human-review metadata without fabricated confidence");
        check(classifier.Classify(Context("多久发货？")).IntentName == "SHIPPING_QUERY",
            "shipping question is classified as SHIPPING_QUERY");
        check(classifier.Classify(Context("支持刻字吗？")).IntentName == "CUSTOMIZATION_QUERY",
            "engraving question is classified as CUSTOMIZATION_QUERY");
        check(classifier.Classify(Context("是什么材质？")).IntentName == "MATERIAL_QUERY",
            "material question is classified as MATERIAL_QUERY");
        check(classifier.Classify(Context("这款多少钱？")).IntentName == "PRICE_QUERY",
            "price question is classified as PRICE_QUERY");
        check(classifier.Classify(Context("刻字后不喜欢能退吗？")).IntentName == "AFTER_SALES_QUERY",
            "after-sales wording takes priority over customization in a mixed question");
        check(classifier.Classify(Context("你好")).IntentName == "OTHER",
            "unknown question uses explicit OTHER fallback without guessing product facts");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV115", "decision_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        DispatchItem Item(string id) => new(id, new string('D', 64),
            new MessageSession(sid, "seller-test", "buyer-test", "fixture", "Test", "", "test-mapping.json"),
            new MessageEnvelope("Qianniu.OCR", "seller-test", "buyer-test", "", [new("buyer", "多久发货？", new(2026, 9, 26, 12, 0, 0))]),
            DateTimeOffset.Now, true, CaptureId: "decision-fixture:frame_000001", AiRequestId: request);
        var failed = Item(new string('E', 64)); var trace = new AgentPipelineTrace(Path.Combine(root, "failure-trace"));
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "failed-state"), Path.Combine(root, "failed-life.jsonl"),
            CancellationToken.None, (_, _, _, _) => { aiCalls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true, "unsafe")); },
            true, trace, new ThrowingClassifier()))
        {
            dispatch.Submit(failed, Context("多久发货？") with { MessageId = failed.MessageId });
            dispatch.Start(); await dispatch.StopAsync();
            check(dispatch.Snapshot().Single().State == "DecisionFailed" && aiCalls == 0 &&
                !File.Exists(Path.Combine(root, "failed-state", "outbox", failed.MessageId, "reply-preview.json")),
                "classifier exception fails closed before AI and creates no preview");
        }
        check(File.ReadAllText(Path.Combine(root, "failure-trace", "agent-pipeline.jsonl")).Contains("DECISION_ERROR"),
            "decision failure has a separate audit event");
        var invalid = Item(new string('F', 64));
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "invalid-state"), Path.Combine(root, "invalid-life.jsonl"),
            CancellationToken.None, (_, _, _, _) => { aiCalls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true, "unsafe")); },
            true, null, new InvalidClassifier()))
        {
            dispatch.Submit(invalid, Context("多久发货？") with { MessageId = invalid.MessageId });
            dispatch.Start(); await dispatch.StopAsync();
            check(dispatch.Snapshot().Single().State == "DecisionFailed" && aiCalls == 0,
                "invalid classifier confidence or risk cannot reach AI");
        }
        var firstFailed = Item(new string('2', 64));
        var later = Item(new string('3', 64));
        var laterCalls = 0;
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "continued-state"),
            Path.Combine(root, "continued-life.jsonl"), CancellationToken.None,
            (_, _, _, _) => { laterCalls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true, "later reply", 10, DateTimeOffset.Now, 1)); },
            true, new AgentPipelineTrace(Path.Combine(root, "continued-trace")), new SelectiveClassifier(firstFailed.MessageId)))
        {
            dispatch.Submit(firstFailed, Context("多久发货？") with { MessageId = firstFailed.MessageId });
            dispatch.Submit(later, Context("多久发货？") with { MessageId = later.MessageId });
            dispatch.Start(); await dispatch.StopAsync();
            check(dispatch.Snapshot().Single(i => i.MessageId == firstFailed.MessageId).State == "DecisionFailed" &&
                dispatch.Snapshot().Single(i => i.MessageId == later.MessageId).State == "Completed" && laterCalls == 1,
                "one decision failure does not prevent a later message from reaching the unchanged AI adapter");
        }        var nonAi = Item(new string('1', 64)) with { AiEnabled = false };
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "offline-state"), Path.Combine(root, "offline-life.jsonl"),
            CancellationToken.None, (_, _, _, _) => Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, false, null)),
            false, null, new ThrowingClassifier()))
        {
            dispatch.Submit(nonAi);
            dispatch.Start(); await dispatch.StopAsync();
            check(dispatch.Snapshot().Single().State == "Completed" &&
                !File.Exists(Path.Combine(root, "offline-state", "outbox", nonAi.MessageId, "decision.json")),
                "AI OFF dispatch never invokes classifier or writes a decision sidecar");
        }
    }
}
