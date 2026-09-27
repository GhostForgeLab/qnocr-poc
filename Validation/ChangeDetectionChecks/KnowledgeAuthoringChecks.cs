using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class KnowledgeAuthoringChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV122", "authoring_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var review = new KnowledgeReviewService(root);
        var author = new KnowledgeAuthoringService(root, review);
        var inputPath = Path.GetFullPath(Path.Combine("Knowledge", "product-authoring.sample.json"));
        var input = JsonSerializer.Deserialize<ProductKnowledgeAuthoringInput>(File.ReadAllText(inputPath))!;
        var draft = author.CreateFromFile(inputPath, "operator-test");
        check(draft.Status == KnowledgeDraftStatus.Draft && draft.DraftId.Length == 32 &&
            draft.SellerId == "seller-test" && draft.ProductId == "1051087533910" &&
            draft.Source.SourceType == KnowledgeSourceType.ManualInput &&
            draft.ConflictingFields.Count == 0,
            "one local JSON product creates an attributed Draft, not published knowledge");
        check(draft.Content.Identity.ProductName == "合成测试钛杯" &&
            draft.Content.Specification?.Capacity?.SequenceEqual(["100ml", "200ml"]) == true &&
            draft.Content.Material?.MaterialName == "TA1纯钛" &&
            draft.Content.Features?.Single().FeatureName == "Engraving",
            "structured input maps Name, Specification, Material and Features into ProductKnowledgeProfile");
        var initial = review.Snapshot();
        check(draft.KnowledgeItemIds.Count == 3 && initial.Count == 3 &&
            initial.All(item => item.Status == KnowledgeStatus.Draft && !item.AiUseAllowed) &&
            initial.Select(item => item.Key).Order().SequenceEqual(new[] { "Capacity", "Feature:Engraving", "Material" }),
            "authoring creates three individually reviewable KnowledgeItem drafts without AI permission");
        var productPath = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.json"));
        var provider = new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(productPath, review.ItemsPath));
        var sessionId = new string('A', 64);
        var context = new SessionContextView(sessionId, "buyer-test", input.ProductId,
            [new EnvelopeMessage("buyer", "这款有200ml规格吗？", new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "authoring-fixture:frame_000001", new string('1', 64), new string('C', 64), 1);
        var request = KnowledgeRequestBuilder.Build(context, new RuleBasedIntentClassifier().Classify(context), input.SellerId);
        check(!(await provider.GetAsync(request)).Success && author.GetDraft(draft.DraftId).Status == KnowledgeDraftStatus.Draft,
            "newly authored facts cannot enter provider before review");
        foreach (var id in draft.KnowledgeItemIds) review.SubmitForReview(id, "operator-test");
        check(author.GetDraft(draft.DraftId).Status == KnowledgeDraftStatus.Reviewing &&
            !(await provider.GetAsync(request)).Success, "draft status reflects Reviewing without exposing facts");
        foreach (var id in draft.KnowledgeItemIds) review.Approve(id, "reviewer-test");
        check(author.GetDraft(draft.DraftId).Status == KnowledgeDraftStatus.Approved &&
            !(await provider.GetAsync(request)).Success, "human approval is separate from publication");
        foreach (var id in draft.KnowledgeItemIds) review.Publish(id, "publisher-test", true);
        var capacity = await provider.GetAsync(request);
        check(author.GetDraft(draft.DraftId).Status == KnowledgeDraftStatus.Published &&
            author.GetDraft(draft.DraftId).AiUseAllowed &&
            capacity.Success && capacity.ReturnedFields?["Capacity"] == "100ml, 200ml" &&
            capacity.ItemSource?.Reference == "test-fixture:v122" && capacity.ProductVersion == "authoring-test-v1",
            "explicit reviewed publication makes the authored Capacity available with provenance and version");
        var material = await provider.GetAsync(request with { NeedFields = ["Material"] });
        var feature = await provider.GetAsync(request with { NeedFields = ["Feature:Engraving"] });
        check(material.Success && material.ReturnedFields?["Material"] == "TA1纯钛" &&
            feature.Success && feature.ReturnedFields?["Feature:Engraving"] == "支持刻字" &&
            feature.KnowledgeCategory == KnowledgeCategories.Feature,
            "published Material and Feature stay in their own typed categories");
        var countBeforeConflict = review.Snapshot().Count;
        var conflict = author.Create(input with { Version = "authoring-test-v2", Capacity = ["300ml"],
            Material = null, Features = null }, "operator-test");
        check(conflict.Status == KnowledgeDraftStatus.Conflict && conflict.ConflictingFields.SequenceEqual(["Capacity"]) &&
            conflict.KnowledgeItemIds.Count == 0 && review.Snapshot().Count == countBeforeConflict &&
            author.GetDraft(conflict.DraftId).Status == KnowledgeDraftStatus.Conflict,
            "conflicting Capacity=300ml is retained for human correction without creating review items");
        check((await provider.GetAsync(request)).ReturnedFields?["Capacity"] == "100ml, 200ml",
            "conflict never replaces published Capacity=200ml knowledge");
        try { author.Create(input with { Material = null, Features = null }, "operator-test");
            throw new Exception("identical duplicate accepted"); }
        catch (InvalidOperationException) { check(true, "identical published facts are not duplicated"); }
        var generated = author.Create(input with { ProductId = "2000000000001", Version = "assistant-test-v1",
            Source = input.Source with { SourceType = KnowledgeSourceType.AssistantGenerated },
            Material = null, Features = null }, "operator-test");
        check(generated.Status == KnowledgeDraftStatus.Draft &&
            review.Snapshot().Single(item => item.KnowledgeId == generated.KnowledgeItemIds.Single()).Status == KnowledgeStatus.Draft,
            "AssistantGenerated source is recorded but cannot self-approve or publish");
        var newProduct = author.Create(input with { ProductId = "2000000000002", Name = "新测试商品",
            Version = "new-product-v1", Capacity = ["300ml"], Material = null, Features = null }, "operator-test");
        var newRequest = request with { ProductReference = new ProductReference("2000000000002",
            "Fixture", DateTimeOffset.Now, context.CaptureId, "ObservedNotVerified") };
        var pendingNewProduct = await provider.GetAsync(newRequest);
        check(!pendingNewProduct.Success && !pendingNewProduct.MissingProduct,
            "a newly authored product exists as a draft but exposes no knowledge");
        foreach (var id in newProduct.KnowledgeItemIds)
        {
            review.SubmitForReview(id, "operator-test");
            review.Approve(id, "reviewer-test");
            review.Publish(id, "publisher-test", true);
        }
        var publishedNewProduct = await provider.GetAsync(newRequest);
        check(publishedNewProduct.Success && publishedNewProduct.ReturnedFields?["Capacity"] == "300ml" &&
            publishedNewProduct.ProductVersion == "new-product-v1" &&
            author.GetDraft(newProduct.DraftId).Status == KnowledgeDraftStatus.Published,
            "a reviewed new ProductId becomes readable without editing the legacy product JSON");        try { author.Create(input with { IsTestData = false }, "operator-test"); throw new Exception("real import accepted"); }
        catch (InvalidDataException) { check(true, "bulk or real product import is excluded from the PoC"); }
        var badPath = Path.Combine(root, "unknown-property.json");
        File.WriteAllText(badPath, File.ReadAllText(inputPath).Replace("\"IsTestData\": true,", "\"IsTestData\": true, \"ApiKey\": \"do-not-import\","));
        try { author.CreateFromFile(badPath, "operator-test"); throw new Exception("unknown property accepted"); }
        catch (JsonException) { check(true, "unexpected credential-like input field is rejected"); }
        var run = Path.Combine(root, "run"); var state = Path.Combine(root, "dispatch");
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "fixture", "Test",
            input.ProductId, "test-mapping.json");
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
                    .GetProperty("Capacity").GetString() == "100ml, 200ml",
                    "AI input contains the explicitly published authored specification");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试商品有100ml和200ml规格。", 20, DateTimeOffset.Now, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(dispatchItem, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", dispatchItem.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.KnowledgeStatus == KnowledgeStatuses.Available &&
            preview.KnowledgeUsedByAi && preview.SendStatus == "Shadow" && preview.Reply.Contains("200ml"),
            "one synthetic AI call produces an authored-knowledge Shadow reply preview");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "authoring and preview create no automatic send request");
    }
}
