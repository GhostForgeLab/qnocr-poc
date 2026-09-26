using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class KnowledgeChecks
{
    private sealed class ThrowingProvider : IKnowledgeProvider
    {
        public int Calls { get; private set; }
        public Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken token = default)
        {
            Calls++; throw new IOException("Injected read-only provider failure");
        }
    }
    private sealed class CountingProvider : IKnowledgeProvider
    {
        public int Calls { get; private set; }
        public Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken token = default)
        {
            Calls++; return new EmptyKnowledgeProvider().GetAsync(request, token);
        }
    }
    private sealed class OtherClassifier : IIntentClassifier
    {
        public IntentResult Classify(SessionContextView context) => new("OTHER", null, false, false, true, "Medium");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var sid = new string('A', 64); var mid = new string('B', 64); var requestId = new string('C', 64);
        var captured = DateTimeOffset.Now;
        var previousRef = new ProductReference("1051087533910", "PriorFrame", captured.AddMinutes(-1),
            "prior-capture", "ObservedNotVerified");
        SessionContextView Context(string question, string productId) => new(sid, "buyer-test", productId,
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 26, 12, 0, 0))],
            captured, "knowledge-fixture:frame_000001", mid, requestId, 1,
            ProductReferences: [previousRef]);
        IIntentClassifier classifier = new RuleBasedIntentClassifier();
        var specContext = Context("这款有200ml规格吗？", "1051087533910");
        var specDecision = classifier.Classify(specContext);
        var specRequest = KnowledgeRequestBuilder.Build(specContext, specDecision);
        check(specDecision.IntentName == "PRODUCT_SPEC_QUERY" && specRequest.SessionId == sid &&
            specRequest.Intent == "PRODUCT_SPEC_QUERY" && specRequest.Query == "这款有200ml规格吗？" &&
            specRequest.NeedFields.SequenceEqual(["Capacity"]) &&
            specRequest.ProductReference?.ProductId == "1051087533910" &&
            specRequest.ProductReference.CaptureId == specContext.CaptureId &&
            specRequest.ProductReference.AssociationStatus == "ObservedNotVerified",
            "specification decision produces Capacity request using only the current observed product reference");
        var missing = await new EmptyKnowledgeProvider().GetAsync(specRequest);
        check(!missing.Success && missing.Source == "None" && missing.Confidence is null &&
            missing.Content == "" && missing.MissingFields.SequenceEqual(["Capacity"]),
            "empty provider returns Missing Capacity without content or invented confidence");
        var shippingContext = Context("多久发货？", "");
        var shippingRequest = KnowledgeRequestBuilder.Build(shippingContext, classifier.Classify(shippingContext));
        check(shippingRequest.Intent == "SHIPPING_QUERY" && shippingRequest.NeedFields.SequenceEqual(["ShippingTime"]) &&
            shippingRequest.ProductReference is null,
            "shipping request names ShippingTime and never reuses a historical product when current ID is empty");
        var shippingMissing = await new EmptyKnowledgeProvider().GetAsync(shippingRequest);
        check(!shippingMissing.Success && shippingMissing.MissingFields.SequenceEqual(["ShippingTime"]),
            "shipping lookup reports the required field missing without blocking later AI");
        check(KnowledgeRequestBuilder.Build(Context("售后怎么处理？", ""),
            new IntentResult("AFTERSALE_QUERY", null, true, true, true, "High")).NeedFields.SequenceEqual(["ReturnPolicy"]),
            "legacy after-sale intent spelling maps to the same read-only ReturnPolicy field");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV116", "knowledge_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        DispatchItem Item(string id, string question) => new(id, new string('D', 64),
            new MessageSession(sid, "seller-test", "buyer-test", "fixture", "Test", "", "test-mapping.json"),
            new MessageEnvelope("Qianniu.OCR", "seller-test", "buyer-test", "", [new("buyer", question, new(2026, 9, 26, 12, 0, 0))]),
            DateTimeOffset.Now, true, CaptureId: "knowledge-fixture:frame_000001", AiRequestId: requestId);
        var failed = Item(new string('E', 64), "多久发货？");
        var provider = new ThrowingProvider(); var aiCalls = 0;
        var trace = new AgentPipelineTrace(Path.Combine(root, "failure-trace"));
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "failure-state"), Path.Combine(root, "failure-life.jsonl"),
            CancellationToken.None, (_, _, _, _) => { aiCalls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                "请以页面显示为准", 10, DateTimeOffset.Now, 1)); }, true, trace,
            knowledgeProvider: provider))
        {
            dispatch.Submit(failed, shippingContext with { MessageId = failed.MessageId });
            dispatch.Start(); await dispatch.StopAsync();
            var previewPath = Path.Combine(root, "failure-state", "outbox", failed.MessageId, "reply-preview.json");
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(previewPath))!;
            check(provider.Calls == 1 && aiCalls == 1 && dispatch.KnowledgeErrors == 1 &&
                dispatch.Snapshot().Single().State == "Completed" && preview.KnowledgeStatus == KnowledgeStatuses.Missing &&
                preview.SendStatus == "Shadow",
                "knowledge provider failure is audited as Missing while unchanged AI still produces a Shadow preview");
            var folder = Path.GetDirectoryName(previewPath)!;
            using var resultDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "knowledge-result.json")));
            check(resultDoc.RootElement.GetProperty("Status").GetString() == "Missing" &&
                resultDoc.RootElement.GetProperty("MissingFields")[0].GetString() == "ShippingTime" &&
                !resultDoc.RootElement.TryGetProperty("Content", out _),
                "durable result stores only missing metadata, not question text or knowledge content");
        }
        var events = File.ReadAllLines(Path.Combine(root, "failure-trace", "agent-pipeline.jsonl"));
        check(events.Any(e => e.Contains("KNOWLEDGE_ERROR")) && events.Any(e => e.Contains("KNOWLEDGE_RESULT")) &&
            events.Any(e => e.Contains("AI_REQUEST")),
            "knowledge error and fallback result are traceable before AI request");
        var other = Item(new string('F', 64), "你好"); var counting = new CountingProvider();
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "not-required-state"), Path.Combine(root, "not-required-life.jsonl"),
            CancellationToken.None, (_, _, _, _) => Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                "您好", 2, DateTimeOffset.Now, 1)), true,
            new AgentPipelineTrace(Path.Combine(root, "not-required-trace")), new OtherClassifier(), counting))
        {
            dispatch.Submit(other, Context("你好", "") with { MessageId = other.MessageId });
            dispatch.Start(); await dispatch.StopAsync();
            var previewPath = Path.Combine(root, "not-required-state", "outbox", other.MessageId, "reply-preview.json");
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(previewPath))!;
            check(counting.Calls == 0 && preview.KnowledgeStatus == KnowledgeStatuses.NotRequired &&
                !File.Exists(Path.Combine(Path.GetDirectoryName(previewPath)!, "knowledge-result.json")),
                "NeedKnowledge=false skips provider and marks preview NotRequired");
        }
    }
}
