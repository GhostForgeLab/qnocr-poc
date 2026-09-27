using System.Text.Json;
using QnocrPoc.Feedback;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class FeedbackRouterChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public void SetText(string text) { }
    }
    private sealed class ThrowingRouter : IFeedbackRouter
    {
        public FeedbackClassification Classify(ReplyFeedback feedback) => throw new InvalidOperationException("injected router failure");
    }

    public static void Run(Action<bool, string> check)
    {
        var router = new RuleBasedFeedbackRouter();
        var id = Guid.NewGuid().ToString("N"); var sid = new string('A', 64);
        ReplyFeedback Feedback(string ai, string final, string type, string category, string? reason = null,
            string? note = null, string? decision = null) => new(id, sid, ai, final, type,
            DateTimeOffset.Now, "fixture-operator", "测试问题", "", 1,
            QuestionCategory: category, Reason: reason, OperatorNote: note, DecisionIntent: decision);
        var gap = router.Classify(Feedback("不知道200ml规格", "这款有200ml规格，可以选200ml。",
            ReplyFeedbackTypes.Edited, "商品规格"));
        check(gap.FeedbackType == FeedbackClassificationTypes.KnowledgeGap &&
            gap.SuggestedTarget == FeedbackTargets.Knowledge && gap.Confidence is null,
            "human-added 200ml fact after uncertain AI reply routes to KnowledgeGap without invented confidence");
        var style = router.Classify(Feedback("尊敬的客户，感谢您的咨询。", "亲，可以的哦。",
            ReplyFeedbackTypes.Edited, "其他", "语气问题"));
        check(style.FeedbackType == FeedbackClassificationTypes.StyleIssue && style.SuggestedTarget == FeedbackTargets.Prompt,
            "manual tone edit routes to StyleIssue and Prompt review");
        var rule = router.Classify(Feedback("这笔订单可以无条件退款。", "定制商品不支持无理由退换，请人工核对。",
            ReplyFeedbackTypes.Edited, "售后", "信息不准确"));
        check(rule.FeedbackType == FeedbackClassificationTypes.RuleIssue && rule.SuggestedTarget == FeedbackTargets.Strategy,
            "after-sales rule correction routes to RuleIssue and Strategy review");
        var intent = router.Classify(Feedback("今天可以发货", "这款有200ml规格", ReplyFeedbackTypes.Edited,
            "商品规格", "信息不准确", decision: "SHIPPING_QUERY"));
        check(intent.FeedbackType == FeedbackClassificationTypes.IntentIssue && intent.SuggestedTarget == FeedbackTargets.Decision,
            "manual category and mismatched Decision intent route to IntentIssue");
        var accepted = router.Classify(Feedback("您好", "您好", ReplyFeedbackTypes.Accepted, "其他"));
        check(accepted.FeedbackType == FeedbackClassificationTypes.Other && accepted.SuggestedTarget == FeedbackTargets.None,
            "accepted unchanged reply contributes no fabricated optimization issue");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV118", "router_" + Guid.NewGuid().ToString("N")));
        var stateRoot = Path.Combine(root, "state"); Directory.CreateDirectory(stateRoot);
        var service = new AssistantReplyService(stateRoot);
        (ReplyPreview Preview, string Folder) Create(string reply, string question, int number)
        {
            var messageId = number.ToString("X").PadLeft(64, '0');
            var context = new SessionContextView(sid, "buyer-test", "",
                [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 27, 12, 0, number))],
                DateTimeOffset.Now, "router-fixture:frame_000001", messageId, Guid.NewGuid().ToString("N"), 1);
            var preview = new ReplyPreview(sid, context.BuyerId, context, reply, null, DateTimeOffset.Now,
                "Shadow", context.CaptureId, context.AiRequestId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                new IntentResult("PRODUCT_SPEC_QUERY", null, true, true, true, "Medium"));
            var folder = Path.Combine(stateRoot, "outbox", messageId); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "reply-preview.json"), JsonSerializer.Serialize(preview));
            return (preview, folder);
        }
        var current = Create("不知道200ml规格", "这款有200ml吗？", 1);
        service.Show(current.Preview.ReplyPreviewId);
        var edited = service.Edit(current.Preview.ReplyPreviewId, "fixture-operator", "这款有200ml规格。");
        service.Approve(current.Preview.ReplyPreviewId, "fixture-operator", edited.ReplySha256);
        service.Copy(current.Preview.ReplyPreviewId, "fixture-operator", edited.ReplySha256, new ClipboardProbe());
        var recorded = service.RecordFeedback(current.Preview.ReplyPreviewId, "fixture-operator",
            ReplyFeedbackTypes.Edited, questionCategory: "商品规格");
        var persisted = JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(Path.Combine(current.Folder, "assistant-feedback.json")))!;
        check(recorded.Classification?.FeedbackType == FeedbackClassificationTypes.KnowledgeGap &&
            persisted.Classification?.SuggestedTarget == FeedbackTargets.Knowledge &&
            persisted.Feedback.DecisionIntent == "PRODUCT_SPEC_QUERY",
            "new human feedback and classification persist together without changing the original preview");
        var audits = File.ReadAllLines(Path.Combine(stateRoot, "assistant-actions.jsonl"));
        using var classifiedEvent = JsonDocument.Parse(audits.Single(line => line.Contains("FEEDBACK_CLASSIFIED")));
        check(classifiedEvent.RootElement.GetProperty("FeedbackType").GetString() == "KnowledgeGap" &&
            classifiedEvent.RootElement.GetProperty("SuggestedTarget").GetString() == "Knowledge" &&
            classifiedEvent.RootElement.GetProperty("Reason").GetString() == "HumanAddedProductFact" &&
            !audits.Single(line => line.Contains("FEEDBACK_CLASSIFIED")).Contains("200ml"),
            "FEEDBACK_CLASSIFIED audit carries preview ID, type, target and reason code without reply text");
        var legacy = Create("不确定材质", "什么材质？", 2);
        var oldFeedback = Feedback("不确定材质", "材质是TA1纯钛", ReplyFeedbackTypes.Edited, "材质")
            with { ReplyPreviewId = legacy.Preview.ReplyPreviewId, SessionId = sid };
        var oldSample = new ConversationSample(legacy.Preview.ReplyPreviewId, sid, legacy.Preview.CaptureId,
            "什么材质？", oldFeedback.OriginalAiReply, oldFeedback.FinalHumanReply, oldFeedback.FeedbackType,
            "", Array.Empty<string>(), 1, DateTimeOffset.Now, DateTimeOffset.Now, "材质");
        File.WriteAllText(Path.Combine(legacy.Folder, "assistant-feedback.json"),
            JsonSerializer.Serialize(new AssistantFeedbackRecord(oldFeedback, oldSample)));
        var before = JsonSerializer.Serialize(oldFeedback);
        var migrated = service.ClassifyExisting(legacy.Preview.ReplyPreviewId);
        var after = JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(Path.Combine(legacy.Folder, "assistant-feedback.json")))!;
        check(migrated.FeedbackType == FeedbackClassificationTypes.KnowledgeGap &&
            JsonSerializer.Serialize(after.Feedback) == before && after.Classification?.FeedbackType == "KnowledgeGap",
            "explicit backfill classifies old feedback without changing human-authored fields");
        var classifiedCount = File.ReadAllLines(Path.Combine(stateRoot, "assistant-actions.jsonl"))
            .Count(line => line.Contains("FEEDBACK_CLASSIFIED"));
        service.ClassifyExisting(legacy.Preview.ReplyPreviewId);
        check(File.ReadAllLines(Path.Combine(stateRoot, "assistant-actions.jsonl"))
            .Count(line => line.Contains("FEEDBACK_CLASSIFIED")) == classifiedCount,
            "classification backfill is idempotent and does not duplicate audit entries");
        var routerError = Create("无法确认", "其他问题", 3);
        var failingService = new AssistantReplyService(stateRoot, new ThrowingRouter());
        var fallback = failingService.RecordFeedback(routerError.Preview.ReplyPreviewId, "fixture-operator",
            ReplyFeedbackTypes.Rejected, questionCategory: "其他", reason: "其他");
        check(fallback.Classification?.FeedbackType == FeedbackClassificationTypes.Other &&
            fallback.Classification.Reason.StartsWith("RouterError_", StringComparison.Ordinal) &&
            File.Exists(Path.Combine(routerError.Folder, "assistant-feedback.json")),
            "router exception preserves human feedback and falls back to an explicit Other classification");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "feedback routing creates no send request or model/knowledge mutation");
    }
}
