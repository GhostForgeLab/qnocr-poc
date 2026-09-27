using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class KnowledgeImportRevisionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV123", "revision_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var review = new KnowledgeReviewService(root);
        var author = new KnowledgeAuthoringService(root, review);
        var imports = new KnowledgeImportService(root, author);
        var source = new KnowledgeSource(KnowledgeSourceType.Import, "synthetic-json",
            "test-fixture:v123-import", DateTimeOffset.UtcNow);
        var raw = File.ReadAllText(Path.Combine("Knowledge", "product-import.sample.json"));
        var request = new KnowledgeImportRequest(Guid.NewGuid().ToString("N"), "seller-test",
            "3000000000010", source, raw, DateTimeOffset.UtcNow);
        var imported = imports.Import(request, "operator-test");
        check(imported.Status == KnowledgeDraftStatus.Draft && imported.KnowledgeItemIds.Count == 3 &&
            imported.Source.SourceType == KnowledgeSourceType.Import &&
            imported.Content.Specification?.Capacity?.Single() == "200ml" &&
            imported.Content.Material?.MaterialName == "TA1纯钛" &&
            imported.Content.Features?.Single().FeatureName == "支持刻字",
            "one external-style JSON request maps to an attributed test KnowledgeDraft and schema");
        var audit = File.ReadAllText(Path.Combine(root, "import-audit.jsonl"));
        check(audit.Contains(request.ImportId) && audit.Contains(imported.DraftId) &&
            !audit.Contains("200ml") && !audit.Contains("TA1纯钛"),
            "import audit links request and draft without RawData or fact values");
        var replay = imports.Import(request, "operator-test");
        check(replay.DraftId == imported.DraftId && review.Snapshot().Count == 3,
            "replaying the same ImportId returns its original draft without duplicate facts");
        try { imports.Import(request with { RawData = raw.Replace("200ml", "300ml") }, "operator-test");
            throw new Exception("ImportId reused for changed payload"); }
        catch (InvalidOperationException) { check(true, "an ImportId cannot be reused for different raw data"); }
        var repository = new JsonProductKnowledgeRepository(Path.Combine("Knowledge", "product-knowledge.json"), review.ItemsPath);
        var provider = new ProductKnowledgeProvider(repository);
        var sessionId = new string('A', 64);
        var context = new SessionContextView(sessionId, "buyer-test", request.ProductId,
            [new EnvelopeMessage("buyer", "容量有多少？", new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "import-fixture:frame_000001", new string('1', 64), new string('C', 64), 1);
        var knowledgeRequest = KnowledgeRequestBuilder.Build(context,
            new RuleBasedIntentClassifier().Classify(context), request.SellerId) with { NeedFields = ["Capacity"] };
        check(!(await provider.GetAsync(knowledgeRequest)).Success,
            "imported Draft cannot enter provider or AI context");
        foreach (var id in imported.KnowledgeItemIds)
        {
            review.SubmitForReview(id, "operator-test");
            review.Approve(id, "reviewer-test");
            review.Publish(id, "publisher-test", true);
        }
        var v1 = await provider.GetAsync(knowledgeRequest);
        check(v1.Success && v1.ReturnedFields?["Capacity"] == "200ml" &&
            v1.ProductVersion == "import-test-v1" && imported.Status == KnowledgeDraftStatus.Draft &&
            author.GetDraft(imported.DraftId).Status == KnowledgeDraftStatus.Published,
            "explicit review publishes v1; persisted draft status resolves from reviewed items");
        var revisionService = new KnowledgeRevisionService(root, review);
        var revisionSource = new KnowledgeSource(KnowledgeSourceType.ManualInput,
            "synthetic-revision", "test-fixture:v123", DateTimeOffset.UtcNow);
        var first = revisionService.Create("seller-test", request.ProductId, "Capacity",
            "300ml", "ManualUpdate", "import-test-v2", revisionSource, "operator-test");
        check(first.Status == KnowledgeRevisionStatus.Draft && first.BaseVersion == "import-test-v1" &&
            first.ChangedFields.Single().OldValue == "200ml" &&
            first.ChangedFields.Single().NewValue == "300ml" &&
            first.ChangedFields.Single().Reason == "ManualUpdate" &&
            first.CandidateKnowledgeId is not null,
            "revision records base version and field-level old/new/reason without publishing");
        var second = new KnowledgeRevisionService(root, review).Create("seller-test", request.ProductId,
            "Capacity", "400ml", "ManualUpdate", "import-test-v3", revisionSource, "operator-test");
        check(second.Status == KnowledgeRevisionStatus.Conflict && second.CandidateKnowledgeId is null &&
            revisionService.GetRevision(second.RevisionId).Status == KnowledgeRevisionStatus.Conflict,
            "second concurrent-style revision of the same active field is Conflict and creates no candidate");
        check((await provider.GetAsync(knowledgeRequest)).ReturnedFields?["Capacity"] == "200ml",
            "v1 remains the default while a v2 revision is pending");
        review.SubmitForReview(first.CandidateKnowledgeId!, "operator-test");
        review.Approve(first.CandidateKnowledgeId!, "reviewer-test");
        check(revisionService.GetRevision(first.RevisionId).Status == KnowledgeRevisionStatus.Approved &&
            (await provider.GetAsync(knowledgeRequest)).ReturnedFields?["Capacity"] == "200ml",
            "Approved v2 still cannot replace active v1 before an explicit publish call");
        var published = revisionService.Publish(first.RevisionId, "publisher-test");
        var v2 = await provider.GetAsync(knowledgeRequest);
        check(published.Status == KnowledgeRevisionStatus.Published &&
            revisionService.GetRevision(first.RevisionId).Status == KnowledgeRevisionStatus.Published &&
            v2.Success && v2.ReturnedFields?["Capacity"] == "300ml" &&
            v2.ProductVersion == "import-test-v2",
            "explicit revision publish switches default provider read to v2");
        try { revisionService.Create("seller-test", request.ProductId, "Capacity", "400ml",
            "ManualUpdate", "import-test-v1", revisionSource, "operator-test");
            throw new Exception("historical version reused"); }
        catch (InvalidOperationException) { check(true, "a historical version identifier cannot be reused for a later revision"); }
        var old = await repository.GetPublishedVersionAsync("seller-test", request.ProductId,
            "Capacity", "import-test-v1");
        var latest = await repository.GetPublishedVersionAsync("seller-test", request.ProductId,
            "Capacity", "import-test-v2");
        check(old is { Value: "200ml", Status: KnowledgeStatus.Published, AiUseAllowed: false } &&
            old.SupersededByKnowledgeId == first.CandidateKnowledgeId &&
            latest is { Value: "300ml", Status: KnowledgeStatus.Published, AiUseAllowed: true },
            "repository retains v1 for explicit audit lookup while only latest v2 is AI-active");
        check(review.Snapshot().Count(item => item.SellerId == "seller-test" &&
            item.ProductId == request.ProductId && item.Key == "Capacity" && item.AiUseAllowed) == 1 &&
            revisionService.GetRevision(second.RevisionId).Status == KnowledgeRevisionStatus.Conflict,
            "two revisions never merge and exactly one active published Capacity remains");
        try { revisionService.Publish(first.RevisionId, "publisher-test"); throw new Exception("duplicate revision publish accepted"); }
        catch (InvalidOperationException) { check(true, "duplicate revision publication is rejected"); }
        try { imports.Import(request with { ImportId = Guid.NewGuid().ToString("N"),
            RawData = raw.Replace("\"IsTestData\": true", "\"IsTestData\": false") }, "operator-test");
            throw new Exception("real import accepted"); }
        catch (InvalidDataException) { check(true, "real or bulk product input is rejected"); }
        try { imports.Import(request with { ImportId = Guid.NewGuid().ToString("N"),
            RawData = raw.Replace("\"IsTestData\": true", "\"IsTestData\": true, \"ApiKey\": \"secret\"") }, "operator-test");
            throw new Exception("unknown import field accepted"); }
        catch (JsonException) { check(true, "unknown credential-like import property is rejected"); }
        var run = Path.Combine(root, "run"); var state = Path.Combine(root, "dispatch");
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "fixture", "Test",
            request.ProductId, "test-mapping.json");
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
                    .GetProperty("Capacity").GetString() == "300ml",
                    "AI input sees only active published v2 Capacity");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试商品新版本容量为300ml。", 20, DateTimeOffset.Now, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(dispatchItem, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", dispatchItem.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.KnowledgeStatus == KnowledgeStatuses.Available &&
            preview.KnowledgeUsedByAi && preview.SendStatus == "Shadow" && preview.Reply.Contains("300ml"),
            "one synthetic AI callback produces a v2-informed Shadow reply preview");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "import and revision workflow create no send request");
    }
}
