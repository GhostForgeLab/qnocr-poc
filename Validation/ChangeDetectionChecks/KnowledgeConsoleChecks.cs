using QnocrPoc.Knowledge;
using QnocrPoc.Models;

internal static class KnowledgeConsoleChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV124", "console_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var review = new KnowledgeReviewService(root);
        var author = new KnowledgeAuthoringService(root, review);
        var source = new KnowledgeSource(KnowledgeSourceType.ManualInput, "KnowledgeConsole",
            "test-fixture:v124", DateTimeOffset.UtcNow);
        var input = new ProductKnowledgeAuthoringInput("seller-test", "4000000000002", "控制台测试商品",
            "console-v1", source, true, ["200ml"], "TA1纯钛",
            [new ProductFeature("Engraving", "支持刻字")], "10cm", "100g",
            [new ProductFAQ("能刻字吗？", "支持刻字")], [new ProductRestriction("仅测试")]);
        var draft = author.Create(input, "operator-test");
        check(draft.Status == KnowledgeDraftStatus.Draft && draft.KnowledgeItemIds.Count == 7 &&
            draft.Content.Specification?.Size == "10cm" && draft.Content.Specification.Weight == "100g" &&
            draft.Content.FAQ?.Single().Answer == "支持刻字" &&
            draft.Content.Restrictions?.Single().RestrictionText == "仅测试",
            "console form model maps specification, material, feature, FAQ and restriction into one Draft");
        check(author.ListDrafts().Any(item => item.DraftId == draft.DraftId),
            "new Draft appears in the console listing");
        var capacity = review.Snapshot().Single(item => item.Key == "Capacity");
        var edited = review.EditDraft(capacity.KnowledgeId, "250ml", "editor-test");
        check(edited.Value == "250ml" && edited.Status == KnowledgeStatus.Draft &&
            File.ReadAllText(review.AuditPath).Contains("KNOWLEDGE_DRAFT_EDITED"),
            "human edit changes only a Draft and records its action");
        var provider = new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(
            Path.Combine("Knowledge", "product-knowledge.json"), review.ItemsPath));
        var request = new KnowledgeRequest(new string('A', 64),
            new ProductReference(input.ProductId, "Fixture", DateTimeOffset.Now,
                "console-fixture:capture", "ObservedNotVerified"), "PRODUCT_SPEC_QUERY", "容量多少？",
            ["Capacity"], new string('1', 64), "console-fixture:capture", DateTimeOffset.Now, input.SellerId);
        check(!(await provider.GetAsync(request)).Success,
            "edited Draft remains unreadable to KnowledgeProvider");
        foreach (var id in draft.KnowledgeItemIds)
        {
            review.SubmitForReview(id, "operator-test");
            review.Approve(id, "reviewer-test");
        }
        check(author.GetDraft(draft.DraftId).Status == KnowledgeDraftStatus.Approved &&
            !(await provider.GetAsync(request)).Success,
            "Approved console items still require a separate publish action");
        try { review.EditDraft(capacity.KnowledgeId, "300ml", "editor-test");
            throw new Exception("approved knowledge edited"); }
        catch (InvalidOperationException) { check(true, "approved item cannot be edited through Draft operation"); }
        foreach (var id in draft.KnowledgeItemIds) review.Publish(id, "publisher-test", true);
        var available = await provider.GetAsync(request);
        check(available.Success && available.ReturnedFields?["Capacity"] == "250ml" &&
            available.ProductVersion == "console-v1" && author.GetDraft(draft.DraftId).AiUseAllowed,
            "manually published console knowledge becomes readable by Provider");
        var revisionService = new KnowledgeRevisionService(root, review);
        var revision = revisionService.Create(input.SellerId, input.ProductId, "Capacity", "300ml",
            "ManualUpdate", "console-v2", source, "operator-test");
        check(revisionService.ListRevisions().Any(item => item.RevisionId == revision.RevisionId) &&
            (await provider.GetAsync(request)).ReturnedFields?["Capacity"] == "250ml",
            "revision appears in console listing while published v1 remains active");
        review.SubmitForReview(revision.CandidateKnowledgeId!, "operator-test");
        review.Approve(revision.CandidateKnowledgeId!, "reviewer-test");
        revisionService.Publish(revision.RevisionId, "publisher-test");
        check((await provider.GetAsync(request)).ReturnedFields?["Capacity"] == "300ml" &&
            (await provider.GetAsync(request)).ProductVersion == "console-v2",
            "explicit revision publication switches Provider to v2");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "console workflow creates no send request");
    }
}
