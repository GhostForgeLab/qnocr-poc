using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class AssistantOperationsChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public int Calls { get; private set; }
        public void SetText(string text) { Calls++; }
    }

    public static void Run(Action<bool, string> check)
    {
        var baseDir = Path.GetFullPath(Path.Combine("Output", "ValidationV113", "operations_" + Guid.NewGuid().ToString("N")));
        var stateRoot = Path.Combine(baseDir, "state");
        var run = Path.Combine(baseDir, "shadow_fixture_run");
        Directory.CreateDirectory(stateRoot); Directory.CreateDirectory(run);
        var started = DateTimeOffset.Now.AddMinutes(-2);
        File.WriteAllText(Path.Combine(run, "session.json"), JsonSerializer.Serialize(new
        {
            StartedAt = started, StateDirectory = stateRoot, AiEnabled = true, SendEnabled = false
        }));
        File.WriteAllText(Path.Combine(run, "summary.json"), JsonSerializer.Serialize(new
        {
            Status = "Completed", ElapsedSeconds = 120.0, SuccessfulFrames = 60,
            BuyerSessions = 2, AiCalls = 3, SentMessages = 0
        }));
        var service = new AssistantReplyService(stateRoot);
        var seller = "seller-fixture";
        var sessionA = new string('A', 64); var sessionB = new string('B', 64);
        var timestamp = new DateTime(2026, 9, 26, 10, 0, 0);
        var q1 = new EnvelopeMessage("buyer", "这款200ml多少钱？", timestamp);
        var q2 = new EnvelopeMessage("buyer", "有现货吗？", timestamp.AddSeconds(10));
        var q3 = new EnvelopeMessage("buyer", "另一款可以刻字吗？", timestamp.AddSeconds(20));
        (ReplyPreview Preview, string Folder) Create(int frame, string sessionId, string buyerId,
            string productId, EnvelopeMessage[] history, string aiReply, ProductReference[]? references = null)
        {
            var captureId = Path.GetFileName(run) + $":frame_{frame:D6}";
            var messageId = frame.ToString("X").PadLeft(64, '0');
            var requestId = Guid.NewGuid().ToString("N");
            var context = new SessionContextView(sessionId, buyerId, productId, history,
                started.AddSeconds(frame * 10), captureId, messageId, requestId,
                history.Length, ProductReferences: references ?? Array.Empty<ProductReference>());
            var preview = new ReplyPreview(sessionId, buyerId, context, aiReply, null,
                started.AddSeconds(frame * 10), "Shadow", captureId, requestId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var folder = Path.Combine(stateRoot, "outbox", messageId);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "reply-preview.json"), JsonSerializer.Serialize(preview));
            var session = new MessageSession(sessionId, seller, buyerId, buyerId, "Test", productId, "fixture");
            var item = new DispatchItem(messageId, new string('F', 64), session,
                new MessageEnvelope("Qianniu.OCR", seller, buyerId, productId, [history[^1]]),
                started.AddSeconds(frame * 10), true, "Completed", CaptureId: captureId, AiCalls: 1);
            File.WriteAllText(Path.Combine(folder, "item.json"), JsonSerializer.Serialize(item));
            service.InitializePreview(folder, preview);
            return (preview, folder);
        }
        var refsA = new[] { new ProductReference("1051087533910", "FixtureObservedOnly", started, "capture-a", "ObservedNotVerified") };
        var first = Create(1, sessionA, "buyer-test:A", "1051087533910", [q1], "您好，价格请以页面为准。", refsA);
        var second = Create(2, sessionA, "buyer-test:A", "", [q1, q2], "有现货，请以页面库存为准。", refsA);
        var third = Create(3, sessionB, "buyer-test:B", "2000000000001", [q3],
            "请提供手机号13812345678和邮箱a@example.com，密钥sk-abcdefghijklmnop。", refsA);
        var beforeFeedback = new AssistantRuntimeDashboard(stateRoot, run).Snapshot();
        check(beforeFeedback.AiSuggestions == 3 && beforeFeedback.FeedbackTotal == 0 &&
            beforeFeedback.AcceptedRate is null && beforeFeedback.EditedRate is null && beforeFeedback.RejectedRate is null,
            "rates remain unavailable before any explicit human feedback");
        check(new AssistantRuntimeDashboard(stateRoot, run).Samples().Count == 3 &&
            new AssistantRuntimeDashboard(stateRoot, run).Samples().All(s => s.FeedbackType == "Unreviewed" && s.FinalHumanReply == ""),
            "unreviewed previews remain visible as redacted samples without inventing a human final reply");
        var clipboard = new ClipboardProbe();
        var firstView = service.Show(first.Preview.ReplyPreviewId)!;
        service.Approve(first.Preview.ReplyPreviewId, "operator-a", firstView.ReplySha256);
        service.Copy(first.Preview.ReplyPreviewId, "operator-a", firstView.ReplySha256, clipboard);
        var accepted = service.RecordFeedback(first.Preview.ReplyPreviewId, "operator-a", ReplyFeedbackTypes.Accepted, productAssessment: "Correct");
        check(accepted.Feedback.OriginalAiReply == accepted.Feedback.FinalHumanReply &&
            accepted.Feedback.FeedbackType == ReplyFeedbackTypes.Accepted,
            "Accepted feedback stores original AI reply and the adopted final reply");
        var secondView = service.Edit(second.Preview.ReplyPreviewId, "operator-b", "有现货，建议先核对页面库存。");
        secondView = service.Edit(second.Preview.ReplyPreviewId, "operator-b", "有现货，具体库存请以商品页为准。");
        service.Approve(second.Preview.ReplyPreviewId, "operator-b", secondView.ReplySha256);
        service.Copy(second.Preview.ReplyPreviewId, "operator-b", secondView.ReplySha256, clipboard);
        var edited = service.RecordFeedback(second.Preview.ReplyPreviewId, "operator-b", ReplyFeedbackTypes.Edited, contextAssessment: "Continued");
        check(edited.Feedback.FinalHumanReply == secondView.EffectiveReply &&
            edited.Feedback.OriginalAiReply != edited.Feedback.FinalHumanReply,
            "Edited feedback records final human text separately from the AI suggestion");
        var rejected = service.RecordFeedback(third.Preview.ReplyPreviewId, "operator-c", ReplyFeedbackTypes.Rejected,
            "我会人工确认，不提供邮箱b@example.com或token=supersecret12345。", productAssessment: "Incorrect");
        var stored = File.ReadAllText(Path.Combine(third.Folder, "assistant-feedback.json"));
        check(rejected.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected &&
            !stored.Contains("13812345678") && !stored.Contains("a@example.com") && !stored.Contains("b@example.com") &&
            !stored.Contains("sk-abcdefghijklmnop"),
            "explicit rejection stores a redacted conversation sample without common credentials/contact details");
        try { service.Approve(third.Preview.ReplyPreviewId, "operator-c", service.Show(third.Preview.ReplyPreviewId)!.ReplySha256);
            throw new Exception("rejected preview unexpectedly approved"); }
        catch (InvalidOperationException) { check(true, "rejected suggestion cannot later be approved or copied"); }
        try { service.RecordFeedback(first.Preview.ReplyPreviewId, "operator-a", ReplyFeedbackTypes.Accepted, productAssessment: "Correct");
            throw new Exception("duplicate feedback unexpectedly recorded"); }
        catch (InvalidOperationException) { check(true, "feedback is immutable and recorded at most once per preview"); }
        var dashboard = new AssistantRuntimeDashboard(stateRoot, run);
        var stats = dashboard.Snapshot();
        check(stats.RuntimeSeconds == 120 && stats.Frames == 60 && stats.ProcessedMessages == 3 &&
            stats.SessionCount == 2 && stats.AiSuggestions == 3 && stats.AiCalls == 3,
            "per-run dashboard reads runtime, frames, messages, sessions and AI suggestions/calls");
        check(stats.HumanEditActions == 2 && stats.DistinctEditedPreviews == 1 && stats.CopyActions == 2
            && stats.AiNotAdopted == 1 && clipboard.Calls == 2,
            "dashboard separates edit actions, edited previews, copies and rejected suggestions");
        check(stats.FeedbackTotal == 3 && stats.FeedbackAccepted == 1 && stats.FeedbackEdited == 1 &&
            stats.FeedbackRejected == 1 && Math.Abs(stats.AcceptedRate!.Value - 1.0 / 3) < 1e-9 &&
            Math.Abs(stats.EditedRate!.Value - 1.0 / 3) < 1e-9 && Math.Abs(stats.RejectedRate!.Value - 1.0 / 3) < 1e-9,
            "accepted/edited/rejected rates use all explicit feedback as denominator");
        check(stats.SessionsWithMultiplePreviews == 1 && stats.ContextContinuousPairs == 1 &&
            stats.ContextUnprovenPairs == 0 && dashboard.Sessions().Single(s => s.SessionId == sessionA).PreviewCount == 2,
            "session observation confirms Q1 is retained in the same buyer context before Q2");
        check(dashboard.Sessions().Single(s => s.SessionId == sessionB).PreviewCount == 1 &&
            !dashboard.Sessions().Single(s => s.SessionId == sessionA).ProductIds.Contains("2000000000001"),
            "different buyer and product references remain in separate session observations");
        check(stats.SuggestionsWithCurrentProductId == 2 && stats.SuggestionsWithoutCurrentProductId == 1 &&
            stats.UnverifiedProductAssociations == 2 && dashboard.Samples().Single(s => s.ReplyPreviewId == second.Preview.ReplyPreviewId).ProductId == "",
            "unknown current product stays empty despite historical references; associations remain unverified");
        check(stats.ProductReferencesMarkedCorrect == 1 && stats.ProductReferencesMarkedIncorrect == 1 &&
            stats.ProductReferencesUnreviewed == 1 && stats.ContextMarkedContinued == 1 &&
            stats.ContextUnreviewed == 2 && stats.AverageGenerationToEditSeconds is > 0,
            "manual product/context assessments and review delay are visible without claiming verified linkage");
        check(stats.SentMessages == 0 && stats.SendSafetySource == "ShadowSummary" &&
            !Directory.EnumerateFiles(stateRoot, "send-request.json", SearchOption.AllDirectories).Any(),
            "dashboard reads Send OFF result and assistant feedback never creates a send request");
        var archive = Path.Combine(stateRoot, "log-archive"); Directory.CreateDirectory(archive);
        File.Move(Path.Combine(stateRoot, "assistant-actions.jsonl"), Path.Combine(archive, "assistant-actions.fixture.jsonl"));
        check(new AssistantRuntimeDashboard(stateRoot, run).Snapshot().HumanEditActions == 2,
            "dashboard retains edit counts after assistant audit archiving");
        var wrongRun = Path.Combine(baseDir, "wrong_run"); Directory.CreateDirectory(wrongRun);
        File.WriteAllText(Path.Combine(wrongRun, "session.json"), JsonSerializer.Serialize(new
        { StartedAt = started, StateDirectory = Path.Combine(baseDir, "other-state"), SendEnabled = false }));
        try { new AssistantRuntimeDashboard(stateRoot, wrongRun).Snapshot(); throw new Exception("wrong state root accepted"); }
        catch (InvalidDataException) { check(true, "dashboard rejects run metadata pointing to a different state root"); }
    }
}
