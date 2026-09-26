using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class AssistantChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public int Calls { get; private set; }
        public string? Text { get; private set; }
        public bool Fail { get; set; }
        public void SetText(string text)
        {
            Calls++;
            if (Fail) throw new IOException("Injected clipboard failure");
            Text = text;
        }
    }

    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV112", "assistant-checks_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var service = new AssistantReplyService(root);
        (ReplyPreview Preview, string Folder) Create(BuyerIdentity? identity, bool product)
        {
            var id = Guid.NewGuid().ToString("N");
            var sessionId = new string('A', 64);
            var refs = product ? new[] { new ProductReference("1051087533910", "Fixture", DateTimeOffset.Now,
                "capture-1", "Observed") } : Array.Empty<ProductReference>();
            var context = new SessionContextView(sessionId, identity?.BuyerId ?? "buyer-test", product ? "1051087533910" : "",
                [new EnvelopeMessage("buyer", "这款怎么样？", new DateTime(2026, 9, 26, 10, 0, 0))],
                DateTimeOffset.Now, "capture-1", new string('B', 64), new string('C', 64), 1,
                "测试买家", ProductReferences: refs, Identity: identity);
            var preview = new ReplyPreview(sessionId, context.BuyerId, context, "您好，可以帮您了解。", null,
                DateTimeOffset.Now, "Shadow", context.CaptureId, context.AiRequestId, Guid.NewGuid().ToString("N"), id);
            var folder = Path.Combine(root, "outbox", id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "reply-preview.json"), JsonSerializer.Serialize(preview));
            return (preview, folder);
        }
        var unverified = Create(null, false);
        var shown = service.Show(unverified.Preview.ReplyPreviewId)!;
        check(shown.Status == AssistedReplyStatuses.AiGenerated && shown.RiskLevel == "High" && shown.BuyerIdentityStatus == "Test"
            && shown.OriginalMessage == "这款怎么样？" && shown.AiReply == unverified.Preview.Reply && shown.SendStatus == "Shadow",
            "assistant view shows buyer, original message, AI reply and high risk for unverified identity");
        check(File.Exists(Path.Combine(unverified.Folder, "assistant-reply.json")) &&
            File.ReadAllText(Path.Combine(root, "assistant-actions.jsonl")).Contains("AI_REPLY_CREATED"),
            "AI-generated assistant state and audit are durable");
        check(service.List().Any(v => v.ReplyPreviewId == unverified.Preview.ReplyPreviewId && v.Status == AssistedReplyStatuses.AiGenerated),
            "assistant list exposes discoverable pending previews without sending");
        var clipboard = new ClipboardProbe();
        try { service.Copy(unverified.Preview.ReplyPreviewId, "operator", shown.ReplySha256, clipboard); throw new Exception("copy unexpectedly succeeded"); }
        catch (InvalidOperationException) { check(clipboard.Calls == 0, "copy is blocked before human approval"); }
        var edited = service.Edit(unverified.Preview.ReplyPreviewId, "operator", "您好，请说明您想了解的方面。");
        check(edited.Status == AssistedReplyStatuses.HumanEdited && edited.EditedBy == "operator" && edited.EditedAt is not null
            && edited.AiReply == unverified.Preview.Reply && edited.EffectiveReply != edited.AiReply,
            "human edit retains original AI text, edited text, operator and time");
        var reloaded = new AssistantReplyService(root).Show(unverified.Preview.ReplyPreviewId)!;
        check(reloaded.EffectiveReply == edited.EffectiveReply && reloaded.ReplySha256 == edited.ReplySha256,
            "human edit survives service restart");
        try { service.Approve(unverified.Preview.ReplyPreviewId, "operator", shown.ReplySha256); throw new Exception("old hash unexpectedly approved"); }
        catch (InvalidOperationException) { check(true, "old AI reply hash cannot approve edited text"); }
        var approved = service.Approve(unverified.Preview.ReplyPreviewId, "operator", edited.ReplySha256);
        check(approved.Status == AssistedReplyStatuses.Approved && approved.ApprovedBy == "operator" && approved.ApprovedAt is not null,
            "approval requires exact current reply and records operator/time");
        var copied = service.Copy(unverified.Preview.ReplyPreviewId, "operator", approved.ReplySha256, clipboard);
        check(copied.Status == AssistedReplyStatuses.Copied && copied.CopiedBy == "operator" && copied.CopiedAt is not null
            && clipboard.Calls == 1 && clipboard.Text == edited.EffectiveReply,
            "approved edited reply is copied once with operator/time");
        var recorded = service.RecordManualOutcome(unverified.Preview.ReplyPreviewId, "operator", "NotSentReported");
        check(recorded.ManualOutcome == "NotSentReported" && recorded.OutcomeBy == "operator" && recorded.OutcomeAt is not null
            && recorded.Status == AssistedReplyStatuses.Copied,
            "operator can record manual result after copy without changing copied state or sending");
        try { service.RecordManualOutcome(unverified.Preview.ReplyPreviewId, "operator", "PastedAndSentReported");
            throw new Exception("duplicate manual outcome unexpectedly accepted"); }
        catch (InvalidOperationException) { check(true, "manual outcome cannot be overwritten without a new preview"); }        var audit = File.ReadAllText(Path.Combine(root, "assistant-actions.jsonl"));
        check(audit.Contains("AI_REPLY_CREATED") && audit.Contains("HUMAN_EDITED") && audit.Contains("HUMAN_APPROVED")
            && audit.Contains("COPY_TO_CLIPBOARD") && audit.Contains("COPY_ACTION") && audit.Contains("MANUAL_OUTCOME_RECORDED"),
            "assistant lifecycle audit records generation, edit, approval and copy");
        check(!File.Exists(Path.Combine(unverified.Folder, "send-request.json")) &&
            !File.Exists(Path.Combine(unverified.Folder, "send-result.json")) &&
            JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(unverified.Folder, "reply-preview.json")))!.Reply == unverified.Preview.Reply,
            "assistant workflow leaves original preview intact and creates no send request/result");
        try { service.Copy(unverified.Preview.ReplyPreviewId, "operator", copied.ReplySha256, clipboard); throw new Exception("duplicate copy unexpectedly succeeded"); }
        catch (InvalidOperationException) { check(clipboard.Calls == 1, "duplicate copy is blocked"); }
        var verified = new BuyerIdentity("platform-id", "Fixture.Platform", 1, DateTimeOffset.Now,
            new string('A', 64), BuyerIdentityStatuses.Verified);
        var medium = Create(verified, false);
        var low = Create(verified, true);
        check(service.Show(medium.Preview.ReplyPreviewId)!.RiskLevel == "Medium" &&
            service.Show(low.Preview.ReplyPreviewId)!.RiskLevel == "Low",
            "risk guidance distinguishes missing product reference from verified identity with reference");
        var failure = Create(null, false);
        var failShown = service.Show(failure.Preview.ReplyPreviewId)!;
        service.Approve(failure.Preview.ReplyPreviewId, "operator", failShown.ReplySha256);
        var failingClipboard = new ClipboardProbe { Fail = true };
        try { service.Copy(failure.Preview.ReplyPreviewId, "operator", failShown.ReplySha256, failingClipboard); throw new Exception("failure not propagated"); }
        catch (IOException) { check(service.Show(failure.Preview.ReplyPreviewId)!.Status == AssistedReplyStatuses.Approved &&
            File.ReadAllText(Path.Combine(root, "assistant-actions.jsonl")).Contains("COPY_FAILED"),
            "clipboard failure is audited without marking copied or sending"); }
    }
}
