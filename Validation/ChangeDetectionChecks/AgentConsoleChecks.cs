using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Product;
using QnocrPoc.Shadow;

internal static class AgentConsoleChecks
{
    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private sealed class TestClipboard : IAssistantClipboard
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV130", "console_" + Guid.NewGuid().ToString("N")));
        var state = Path.Combine(root, "state"); var run = Path.Combine(root, "shadow_agent_console_fixture");
        Directory.CreateDirectory(run);
        var sid = new string('A', 64); var buyerId = "buyer-test:fixture"; var productId = "1051087533910";
        var now = DateTimeOffset.UtcNow;
        var original = new MessageSession(sid, "seller-test", buyerId, "buyer-fixture", "Test", "", null,
            "buyer-fixture", now, now);
        var sessionPath = Path.Combine(state, "sessions", sid, "session.json");
        WriteJson(sessionPath, original);
        var manual = new ManualProductResolver(state);
        var binding = manual.Bind(sid, "seller-test", productId, "合成测试商品", "operator-test", now);
        var session = original with { CurrentProductContext = binding,
            ProductContextBindingStatus = ProductContextBindingStatus.Bound };
        WriteJson(sessionPath, session);
        File.WriteAllText(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new
        { StateDirectory = state, SendEnabled = false, SentMessages = 0 }));
        var assistant = new AssistantReplyService(state);
        var trace = new AgentPipelineTrace(run);
        var previews = new List<ReplyPreview>();
        for (var index = 1; index <= 3; index++)
        {
            var messageId = new string((char)('0' + index), 64);
            var captureId = Path.GetFileName(run) + ":frame_" + index.ToString("D6");
            var message = new EnvelopeMessage("buyer", index switch
            { 1 => "这款有200ml吗？", 2 => "是什么材质？", _ => "什么时候发货？" },
                new DateTime(2026, 9, 27, 12, 0, index));
            var context = new SessionContextView(sid, buyerId, productId, [message], now.AddSeconds(index),
                captureId, messageId, new string('C', 64), 1, "buyer-fixture",
                now, now.AddSeconds(index), CurrentProductContext: binding,
                ProductContextBindingStatus: ProductContextBindingStatus.Bound);
            var preview = new ReplyPreview(sid, buyerId, context, "AI测试建议" + index, null,
                now.AddSeconds(index), "Shadow", captureId, context.AiRequestId,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                new IntentResult(index == 2 ? "MATERIAL_QUERY" : index == 3 ? "SHIPPING_QUERY" : "PRODUCT_SPEC_QUERY",
                    0.9, true, true, true, "Medium", "JEV/fixture", "choice"),
                index == 3 ? "Missing" : "Available", "LocalTestProductConfig", index != 3,
                false, "test-v1", false, "Specification");
            var folder = Path.Combine(state, "outbox", messageId);
            Directory.CreateDirectory(folder);
            WriteJson(Path.Combine(folder, "envelope.json"), new MessageEnvelope(
                "Qianniu.OCR", "seller-test", buyerId, "", [message]));
            WriteJson(Path.Combine(folder, "reply-preview.json"), preview);
            assistant.InitializePreview(folder, preview);
            trace.IntentProviderObserved(new IntentProviderTrace("INTENT_PROVIDER_RESULT", "JEV",
                preview.Decision!.IntentName, false, now.AddSeconds(index), sid, messageId,
                captureId, LatencyMilliseconds: 120 + index));
            previews.Add(preview);
        }
        var reader = new AgentConsoleReadService(state, run);
        var sessions = reader.ListSessions();
        check(sessions.Count == 1 && sessions[0].SessionId == sid &&
            sessions[0].Buyer == "buyer-fixture" && sessions[0].PreviewCount == 3 &&
            sessions[0].LastMessage == "什么时候发货？",
            "Agent Console session list shows buyer, last message, updated time and preview count");
        var conversation = reader.GetConversation(sid);
        check(conversation.Timeline.Count == 3 &&
            conversation.Timeline.All(item => item.Role == "buyer" && item.Source == "Qianniu.OCR") &&
            conversation.Timeline.Select(item => item.Text).SequenceEqual(new[]
            { "这款有200ml吗？", "是什么材质？", "什么时候发货？" }),
            "conversation view shows captured buyer message content, time and source in order");
        var firstRow = conversation.Previews.Single(item => item.MessageId == previews[0].Input.MessageId);
        check(firstRow.IntentProvider == "JEV" && !firstRow.FallbackUsed &&
            firstRow.IntentLatencyMilliseconds == 121 &&
            firstRow.View.DecisionIntent == "PRODUCT_SPEC_QUERY" && firstRow.View.Decision?.Confidence == 0.9 &&
            firstRow.ProductContext?.ProductId == productId && firstRow.View.KnowledgeStatus == "Available" &&
            firstRow.View.KnowledgeUsedByAi && firstRow.View.AiReply == "AI测试建议1",
            "AI pane projection includes intent, provider, confidence, current product, knowledge and reply");
        check(reader.RequirePreviewSession(previews[0].ReplyPreviewId) == sid,
            "write guard resolves preview only within this run");
        try { reader.RequirePreviewSession(Guid.NewGuid().ToString("N"));
            throw new Exception("foreign preview accepted"); }
        catch (KeyNotFoundException) { check(true, "write guard rejects a preview outside the selected run"); }
        var edited = assistant.Edit(previews[0].ReplyPreviewId, "editor-test", "人工修改后的答复");
        check(edited.Status == AssistedReplyStatuses.HumanEdited && edited.EditedReply == "人工修改后的答复",
            "human edit persists separately from the original AI suggestion");
        assistant.Approve(previews[0].ReplyPreviewId, "reviewer-test", edited.ReplySha256);
        var clipboard = new TestClipboard();
        var copied = assistant.Copy(previews[0].ReplyPreviewId, "operator-test", edited.ReplySha256, clipboard);
        var editedFeedback = assistant.RecordFeedback(previews[0].ReplyPreviewId, "operator-test", ReplyFeedbackTypes.Edited);
        check(copied.Status == AssistedReplyStatuses.Copied && clipboard.Text == "人工修改后的答复" &&
            editedFeedback.Feedback.ProductId == productId &&
            editedFeedback.Feedback.OriginalAiReply == "AI测试建议1" &&
            editedFeedback.Feedback.FinalHumanReply == "人工修改后的答复" &&
            editedFeedback.Feedback.FeedbackType == ReplyFeedbackTypes.Edited,
            "edited reply is approved, copied once and linked to product-scoped human feedback");
        var second = assistant.Show(previews[1].ReplyPreviewId)!;
        assistant.Approve(previews[1].ReplyPreviewId, "reviewer-test", second.ReplySha256);
        var acceptedClipboard = new TestClipboard();
        assistant.Copy(previews[1].ReplyPreviewId, "operator-test", second.ReplySha256, acceptedClipboard);
        var accepted = assistant.RecordFeedback(previews[1].ReplyPreviewId, "operator-test", ReplyFeedbackTypes.Accepted);
        var rejected = assistant.RecordFeedback(previews[2].ReplyPreviewId, "operator-test", ReplyFeedbackTypes.Rejected,
            rejectedFinalReply: "人工另行处理");
        check(accepted.Feedback.FeedbackType == ReplyFeedbackTypes.Accepted &&
            accepted.Feedback.FinalHumanReply == "AI测试建议2" &&
            rejected.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected &&
            rejected.Feedback.FinalHumanReply == "人工另行处理" &&
            reader.GetConversation(sid).Previews.All(item => item.FeedbackType is not null),
            "Accepted, Edited and Rejected feedback remain visible and retain human final text");
        var audit = File.ReadAllText(Path.Combine(state, "assistant-actions.jsonl"));
        check(audit.Contains("HUMAN_EDITED") && audit.Contains("COPY_ACTION") &&
            audit.Contains("FEEDBACK_RECORDED"),
            "existing assistant audit records edit, copy and feedback actions");
        reader.RequireVisibleSession(sid);
        manual.Clear(sid, "operator-test");
        check(reader.GetConversation(sid).Session.CurrentProductContext is null &&
            reader.GetConversation(sid).Session.ProductContextBindingStatus == ProductContextBindingStatus.Unknown,
            "clearing a manual product immediately updates the current session panel");
        try { _ = new AgentConsoleReadService(Path.Combine(root, "wrong-state"), run);
            throw new Exception("mismatched run/state accepted"); }
        catch (InvalidDataException) { check(true, "Agent Console rejects a mismatched run/state pair"); }
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "Agent Console review and clipboard flow creates no send request");
    }
}
