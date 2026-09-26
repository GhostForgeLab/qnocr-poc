using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class EvaluationBatchChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public int Calls { get; private set; }
        public void SetText(string text) { Calls++; }
    }

    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV114", "batch_" + Guid.NewGuid().ToString("N")));
        var state = Path.Combine(root, "state"); var run = Path.Combine(root, "shadow_fixture_v114");
        Directory.CreateDirectory(state); Directory.CreateDirectory(run);
        var started = DateTimeOffset.Now.AddMinutes(-2);
        File.WriteAllText(Path.Combine(run, "session.json"), JsonSerializer.Serialize(new
        { StartedAt = started, StateDirectory = state, AiEnabled = true, SendEnabled = false }));
        File.WriteAllText(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new
        { Status = "Completed", ElapsedSeconds = 120.0, SuccessfulFrames = 50, BuyerSessions = 2, AiCalls = 3, SentMessages = 0 }));
        var unsafeRun = Path.Combine(root, "unsafe_run"); Directory.CreateDirectory(unsafeRun);
        File.WriteAllText(Path.Combine(unsafeRun, "session.json"), JsonSerializer.Serialize(new
        { StartedAt = started, StateDirectory = state, SendEnabled = true }));
        var unsafeBatches = new EvaluationBatchService(state);
        try { unsafeBatches.Start(unsafeRun, "IsolatedFixture", "operator-v114");
            throw new Exception("send-enabled run entered evaluation batch"); }
        catch (InvalidDataException) { check(true, "batch creation rejects any run without explicit Send OFF metadata"); }        var batches = new EvaluationBatchService(state);
        var batch = batches.Start(run, "IsolatedFixture", "operator-v114");
        check(batch.BatchId.Length == 32 && batch.Status == "Open" && batch.EndTime is null &&
            batch.Environment == "IsolatedFixture" && batch.StartTime > started && batch.Operator == "operator-v114",
            "evaluation batch records unique ID, start, environment and operator");
        try { batches.Start(run, "IsolatedFixture", "operator-v114"); throw new Exception("duplicate run batch accepted"); }
        catch (InvalidOperationException) { check(true, "one Shadow run cannot enter two evaluation batches"); }
        var assistant = new AssistantReplyService(state);
        (ReplyPreview Preview, string Folder) Create(int frame, string sessionId, string text, string productId)
        {
            var captureId = Path.GetFileName(run) + $":frame_{frame:D6}";
            var messageId = frame.ToString("X").PadLeft(64, '0');
            var context = new SessionContextView(sessionId, "buyer-test:" + sessionId[..8], productId,
                [new EnvelopeMessage("buyer", text, new DateTime(2026, 9, 26, 12, 0, frame))],
                started.AddSeconds(frame * 10), captureId, messageId, Guid.NewGuid().ToString("N"), 1);
            var preview = new ReplyPreview(sessionId, context.BuyerId, context, "AI建议" + frame, null,
                started.AddSeconds(frame * 10 + 1), "Shadow", captureId, context.AiRequestId,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var folder = Path.Combine(state, "outbox", messageId); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "reply-preview.json"), JsonSerializer.Serialize(preview));
            var session = new MessageSession(sessionId, "seller-test", context.BuyerId, "测试买家", "Test", productId, "fixture");
            var item = new DispatchItem(messageId, new string('F', 64), session,
                new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, productId, [context.Messages[0]]),
                started.AddSeconds(frame * 10), true, "Completed", CaptureId: captureId, AiCalls: 1);
            File.WriteAllText(Path.Combine(folder, "item.json"), JsonSerializer.Serialize(item));
            assistant.InitializePreview(folder, preview);
            return (preview, folder);
        }
        var sessionA = new string('A', 64); var sessionB = new string('B', 64);
        var accepted = Create(1, sessionA, "多少钱？", "1051087533910");
        var edited = Create(2, sessionA, "什么时候发货？", "1051087533910");
        var rejected = Create(3, sessionB, "刻字后能退吗？", "");
        var clipboard = new ClipboardProbe();
        var acceptedView = assistant.Show(accepted.Preview.ReplyPreviewId)!;
        assistant.Approve(accepted.Preview.ReplyPreviewId, "operator-v114", acceptedView.ReplySha256);
        assistant.Copy(accepted.Preview.ReplyPreviewId, "operator-v114", acceptedView.ReplySha256, clipboard);
        try { batches.RecordFeedback(batch.BatchId, accepted.Preview.ReplyPreviewId, "wrong-operator",
                ReplyFeedbackTypes.Accepted, "价格", null); throw new Exception("wrong operator accepted"); }
        catch (InvalidOperationException) { check(true, "feedback operator must match the batch operator"); }
        var acceptedFeedback = batches.RecordFeedback(batch.BatchId, accepted.Preview.ReplyPreviewId,
            "operator-v114", ReplyFeedbackTypes.Accepted, "价格", null, productAssessment: "Correct",
            operatorNote: "客服确认符合实际；勿保存a@example.com");
        check(acceptedFeedback.Feedback.BatchId == batch.BatchId && acceptedFeedback.Feedback.QuestionCategory == "价格"
            && acceptedFeedback.Feedback.OperatorNote?.Contains("[REDACTED_EMAIL]") == true
            && acceptedFeedback.Feedback.FinalHumanReply == acceptedFeedback.Feedback.OriginalAiReply,
            "accepted AI reply is linked to its batch with a human category");
        var editView = assistant.Edit(edited.Preview.ReplyPreviewId, "operator-v114", "人工修改后的发货答复");
        assistant.Approve(edited.Preview.ReplyPreviewId, "operator-v114", editView.ReplySha256);
        assistant.Copy(edited.Preview.ReplyPreviewId, "operator-v114", editView.ReplySha256, clipboard);
        try { batches.RecordFeedback(batch.BatchId, edited.Preview.ReplyPreviewId,
                "operator-v114", ReplyFeedbackTypes.Edited, "发货", null); throw new Exception("missing reason accepted"); }
        catch (ArgumentException) { check(true, "edited feedback requires an explicit human reason"); }
        var editedFeedback = batches.RecordFeedback(batch.BatchId, edited.Preview.ReplyPreviewId,
            "operator-v114", ReplyFeedbackTypes.Edited, "发货", "语气问题");
        check(editedFeedback.Feedback.Reason == "语气问题" && editedFeedback.Sample.FinalHumanReply == "人工修改后的发货答复",
            "edited reply preserves its final human text and reason");
        var rejectedFeedback = batches.RecordFeedback(batch.BatchId, rejected.Preview.ReplyPreviewId,
            "operator-v114", ReplyFeedbackTypes.Rejected, "售后", "缺少商品信息", "需要人工核对退货条件");
        check(rejectedFeedback.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected &&
            rejectedFeedback.Feedback.QuestionCategory == "售后" && rejectedFeedback.Feedback.ProductId == "",
            "rejected answer records category/reason without inventing a product reference");
        var report = batches.Report(batch.BatchId);
        check(report.TotalConsultations == 2 && report.BuyerMessages == 3 && report.AiSuggestions == 3 &&
            report.FeedbackTotal == 3 && report.Accepted == 1 && report.Edited == 1 && report.Rejected == 1 && report.Unreviewed == 0,
            "batch report counts consultations, messages, suggestions and each human outcome");
        check(Math.Abs(report.AiAdoptionRate!.Value - 1.0 / 3) < 1e-9 &&
            Math.Abs(report.HumanEditRate!.Value - 1.0 / 3) < 1e-9 && report.FeedbackCoverage == 1,
            "batch rates use explicit reviewed feedback and expose feedback coverage");
        check(report.QuestionCategories["价格"] == 1 && report.QuestionCategories["发货"] == 1 &&
            report.QuestionCategories["售后"] == 1 && report.FeedbackReasons["语气问题"] == 1 &&
            report.FeedbackReasons["缺少商品信息"] == 1,
            "human question categories and reasons are grouped without automatic analysis");
        check(report.RejectedQuestions.Count == 1 && report.MissingProductInformationQuestions.Count == 1 &&
            report.RejectedQuestions[0].BuyerQuestion == "刻字后能退吗？",
            "rejected questions and manual missing-product tags remain reviewable as redacted samples");
        check(report.AiCalls == 3 && report.SentMessages == 0 && clipboard.Calls == 2 &&
            !Directory.EnumerateFiles(state, "send-request.json", SearchOption.AllDirectories).Any(),
            "batch operations do not call a send adapter or create send requests");
        var closed = batches.End(batch.BatchId, "operator-v114");
        check(closed.Status == "Closed" && closed.EndTime is not null && closed.EndTime >= closed.StartTime,
            "batch end records operator-controlled end time");
        try { batches.End(batch.BatchId, "operator-v114"); throw new Exception("duplicate end accepted"); }
        catch (InvalidOperationException) { check(true, "closed batch cannot be closed again"); }
        try { batches.RecordFeedback(batch.BatchId, Guid.NewGuid().ToString("N"), "operator-v114",
                ReplyFeedbackTypes.Rejected, "其他", "其他"); throw new Exception("closed batch accepted feedback"); }
        catch (InvalidOperationException) { check(true, "closed batch rejects later feedback"); }
        check(File.ReadAllText(Path.Combine(state, "evaluation-batches", "batch-events.jsonl")).Contains("BATCH_FEEDBACK_RECORDED"),
            "batch start, feedback and end are audited without reply text or credentials");
    }
}
