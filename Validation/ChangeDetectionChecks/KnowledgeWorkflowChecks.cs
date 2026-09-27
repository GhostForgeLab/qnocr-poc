using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class KnowledgeWorkflowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV121", "workflow_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var service = new KnowledgeReviewService(Path.Combine(root, "state"));
        var source = new KnowledgeSource(KnowledgeSourceType.ManualInput, "synthetic-review-fixture",
            "test-fixture:v121", DateTimeOffset.UtcNow);
        var capacity = service.CreateDraft("seller-test", "1051087533910", KnowledgeCategories.Specification,
            "Capacity", "200ml", source, "test-v2", "operator-test");
        check(capacity.Status == KnowledgeStatus.Draft && !capacity.AiUseAllowed &&
            capacity.Version.Status == KnowledgeStatus.Draft && capacity.Source.SourceType == KnowledgeSourceType.ManualInput,
            "a created item records provenance, version and Draft status without AI permission");
        var productPath = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.json"));
        var repository = new JsonProductKnowledgeRepository(productPath, service.ItemsPath);
        var provider = new ProductKnowledgeProvider(repository);
        var sessionId = new string('A', 64);
        var context = new SessionContextView(sessionId, "buyer-test", "1051087533910",
            [new EnvelopeMessage("buyer", "这款有200ml规格吗？", new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "workflow-fixture:frame_000001", new string('1', 64), new string('C', 64), 1);
        var request = KnowledgeRequestBuilder.Build(context, new RuleBasedIntentClassifier().Classify(context), "seller-test");
        async Task<KnowledgeResult> Read() => await provider.GetAsync(request);
        var draft = await Read();
        check(!draft.Success && draft.MissingFields.SequenceEqual(["Capacity"]) && !draft.Content.Contains("200ml"),
            "Draft is unreadable even though legacy Fields contain a capacity value");
        var reviewing = service.SubmitForReview(capacity.KnowledgeId, "operator-test");
        check(reviewing.Status == KnowledgeStatus.Reviewing && !(await Read()).Success,
            "Reviewing remains unavailable to AI");
        var approved = service.Approve(capacity.KnowledgeId, "reviewer-test");
        check(approved.Status == KnowledgeStatus.Approved && approved.Version.ApprovedBy == "reviewer-test" &&
            !(await Read()).Success, "human Approved status alone does not publish knowledge");
        var unpublished = service.Publish(capacity.KnowledgeId, "publisher-test", allowAiUse: false);
        check(unpublished.Status == KnowledgeStatus.Published && !unpublished.AiUseAllowed &&
            !(await Read()).Success, "Published without explicit AI-use permission remains unavailable");
        try { service.Publish(capacity.KnowledgeId, "publisher-test", true); throw new Exception("duplicate publish accepted"); }
        catch (InvalidOperationException) { check(true, "duplicate publication is rejected"); }
        var material = service.CreateDraft("seller-test", "1051087533910", KnowledgeCategories.Material,
            "Material", "unapproved material", source, "test-v2", "operator-test");
        service.SubmitForReview(material.KnowledgeId, "operator-test");
        var rejected = service.Reject(material.KnowledgeId, "reviewer-test", "synthetic rejection");
        var materialResult = await provider.GetAsync(request with { NeedFields = ["Material"] });
        check(rejected.Status == KnowledgeStatus.Rejected && !materialResult.Success &&
            materialResult.MissingFields.SequenceEqual(["Material"]),
            "Rejected item cannot leak through legacy Material fallback");
        try { service.Approve(material.KnowledgeId, "reviewer-test"); throw new Exception("rejected item approved"); }
        catch (InvalidOperationException) { check(true, "Rejected item cannot advance to approval"); }
        var publishable = service.CreateDraft("seller-test", "1051087533910", KnowledgeCategories.Specification,
            "Capacity", "200ml", source, "test-v3", "operator-test");
        service.SubmitForReview(publishable.KnowledgeId, "operator-test");
        service.Approve(publishable.KnowledgeId, "reviewer-test");
        var published = service.Publish(publishable.KnowledgeId, "publisher-test", allowAiUse: true);
        var available = await Read();
        check(published.Status == KnowledgeStatus.Published && published.AiUseAllowed &&
            available.Success && available.ReturnedFields?["Capacity"] == "200ml" &&
            available.KnowledgeId == publishable.KnowledgeId && available.ProductVersion == "test-v3" &&
            available.ItemSource?.Reference == "test-fixture:v121",
            "only explicitly published test Capacity enters a versioned provenance-bearing KnowledgeResult");
        var duplicate = service.CreateDraft("seller-test", "1051087533910", KnowledgeCategories.Specification,
            "Capacity", "300ml", source, "test-v4", "operator-test");
        service.SubmitForReview(duplicate.KnowledgeId, "operator-test");
        service.Approve(duplicate.KnowledgeId, "reviewer-test");
        try { service.Publish(duplicate.KnowledgeId, "publisher-test", true); throw new Exception("duplicate field published"); }
        catch (InvalidOperationException) { check(true, "a second active published version of the same field is rejected"); }
        check((await provider.GetAsync(request with { SellerId = "seller-other" })).MissingProduct &&
            !(await provider.GetAsync(request with { NeedFields = ["Material"] })).Success,
            "workflow facts never cross seller scope or fill a rejected field");
        var reopened = new KnowledgeReviewService(Path.Combine(root, "state"));
        check(reopened.Snapshot().Count == 4 && reopened.Snapshot().Single(x => x.KnowledgeId == publishable.KnowledgeId).Status == KnowledgeStatus.Published,
            "workflow state persists across service restart");
        var audit = File.ReadAllText(service.AuditPath);
        check(new[] { "KNOWLEDGE_CREATED", "KNOWLEDGE_REVIEWED", "KNOWLEDGE_PUBLISHED", "KNOWLEDGE_REJECTED" }
            .All(audit.Contains) && !audit.Contains("200ml") && !audit.Contains("unapproved material"),
            "audit contains required lifecycle events without fact values");
        try { await new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(productPath,
            Path.Combine(root, "missing-state.json"))).GetAsync(request); throw new Exception("missing state fallback accepted"); }
        catch (InvalidDataException) { check(true, "missing workflow state fails closed instead of exposing legacy facts"); }
        var legacy = await new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(productPath)).GetAsync(request);
        check(legacy.Success && legacy.ReturnedFields?["Capacity"] == "100ml, 200ml",
            "legacy local test repository remains readable when workflow is not explicitly enabled");
        var run = Path.Combine(root, "run"); var state = Path.Combine(root, "dispatch");
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "fixture", "Test",
            "1051087533910", "test-mapping.json");
        var dispatchItem = new DispatchItem(context.MessageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, session.ProductId,
                [context.Messages[^1]]), DateTimeOffset.Now, true,
            CaptureId: context.CaptureId, AiRequestId: context.AiRequestId);
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
            (_, _, attempt, _) =>
            {
                aiCalls++;
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                check(model.RootElement.GetProperty("Knowledge").GetProperty("FactFields")
                    .GetProperty("Capacity").GetString() == "200ml", "AI input contains only published capacity");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试知识显示有200ml。", 20, DateTimeOffset.Now, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(dispatchItem, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", dispatchItem.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.KnowledgeStatus == KnowledgeStatuses.Available &&
            preview.KnowledgeUsedByAi && preview.SendStatus == "Shadow" && preview.Reply.Contains("200ml"),
            "one synthetic AI call creates a Shadow reply preview from published knowledge");
        var trace = File.ReadAllText(Path.Combine(run, "agent-pipeline.jsonl"));
        check(trace.Contains(publishable.KnowledgeId) && trace.Contains("test-fixture:v121") &&
            trace.Contains("KNOWLEDGE_RESULT"), "AI knowledge trace links the published item and its source without logging its value");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "no send request or automatic send is created");
    }
}
