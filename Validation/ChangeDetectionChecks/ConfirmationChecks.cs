using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ConfirmationChecks
{
    private sealed class RecordingAdapter(bool succeed) : IReplySendAdapter
    {
        public string Mode => "Isolated";
        public int Calls { get; private set; }
        public Task<SendAdapterResult> SendAsync(ConfirmedSendRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new SendAdapterResult(succeed ? "IsolatedRecorded" : "SimulatedFailure",
                succeed, false, succeed ? "Local record only" : "Injected failure", DateTimeOffset.Now));
        }
    }

    private sealed class ProbePort : IQnbotSendPort
    {
        public int Calls { get; private set; }
        public Task<bool> SendTextWithRetryAsync(string sellerNick, string buyerNick, string text, CancellationToken token)
        {
            Calls++; return Task.FromResult(true);
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV17", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var service = new PreviewConfirmationService(root);
        ReplyPreview Create(string reply)
        {
            var id = Guid.NewGuid().ToString("N");
            var context = new SessionContextView(new string('A', 64), "buyer-test:A", "", [new("buyer", "测试问题", new(2026, 9, 25, 10, 0, 0))],
                DateTimeOffset.Now, "test-run:frame_000001", new string('B', 64), new string('C', 64), 1);
            var preview = new ReplyPreview(context.SessionId, context.BuyerId, context, reply, null, DateTimeOffset.Now,
                "Shadow", context.CaptureId, context.AiRequestId, Guid.NewGuid().ToString("N"), id);
            var folder = Path.Combine(root, "outbox", context.MessageId + id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "reply-preview.json"), JsonSerializer.Serialize(preview));
            return preview;
        }

        var preview = Create("隔离测试回复，不发送千牛");
        var adapter = new RecordingAdapter(true);
        var shown = service.Show(preview.ReplyPreviewId);
        check(shown?.Delivery.Status == ReplyDeliveryStatuses.WaitingConfirmation && shown.Delivery.ReplySha256 == PreviewConfirmationService.ReplySha256(preview.Reply)
            && adapter.Calls == 0, "new Shadow preview waits for explicit review and never sends on display");
        var missing = await service.ConfirmSendAsync(Guid.NewGuid().ToString("N"), "test-operator", shown!.Delivery.ReplySha256, adapter);
        check(missing.Status == "PREVIEW_NOT_FOUND" && adapter.Calls == 0, "unknown preview records PREVIEW_NOT_FOUND without adapter call");
        var mismatch = await service.ConfirmSendAsync(preview.ReplyPreviewId, "test-operator", new string('0', 64), adapter);
        check(mismatch.Status == "CONFIRM_TEXT_MISMATCH" && adapter.Calls == 0,
            "human confirmation must match the exact reviewed reply hash");
        var accepted = await service.ConfirmSendAsync(preview.ReplyPreviewId, "test-operator", shown.Delivery.ReplySha256, adapter);
        check(accepted.Status == ReplyDeliveryStatuses.Sent && adapter.Calls == 1 && accepted.Delivery?.ConfirmedBy == "test-operator"
            && accepted.Delivery.ConfirmedAt is not null && accepted.Delivery.HostUser is not null && accepted.SendResult?.ActualSent == false,
            "explicit confirmation records operator/time and enters isolated send adapter once");
        var duplicate = await service.ConfirmSendAsync(preview.ReplyPreviewId, "test-operator", shown.Delivery.ReplySha256, adapter);
        check(duplicate.Status == "DUPLICATE_CONFIRM" && adapter.Calls == 1,
            "repeat confirmation cannot invoke the adapter twice");
        var folder = Directory.EnumerateDirectories(Path.Combine(root, "outbox"))
            .Single(path => JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(path, "reply-preview.json")))?.ReplyPreviewId == preview.ReplyPreviewId);
        var request = JsonSerializer.Deserialize<ConfirmedSendRequest>(File.ReadAllText(Path.Combine(folder, "send-request.json")))!;
        check(request.Reply == preview.Reply && request.CaptureId == preview.CaptureId && request.SessionId == preview.SessionId
            && request.AiRequestId == preview.AiRequestId && request.ReplyPreviewId == preview.ReplyPreviewId
            && request.SendRequestId == accepted.Delivery!.SendRequestId,
            "send request durably links capture, session, AI request, preview and send request IDs with exact text");

        var failedPreview = Create("失败路径");
        var failedAdapter = new RecordingAdapter(false);
        var failed = await service.ConfirmSendAsync(failedPreview.ReplyPreviewId, "test-operator",
            PreviewConfirmationService.ReplySha256(failedPreview.Reply), failedAdapter);
        check(failed.Status == ReplyDeliveryStatuses.Failed && failedAdapter.Calls == 1 && failed.SendResult?.ActualSent == false,
            "send failure persists Failed and never claims real delivery");
        var cancelledPreview = Create("取消路径");
        var cancelled = service.Cancel(cancelledPreview.ReplyPreviewId, "test-operator");
        var afterCancel = await service.ConfirmSendAsync(cancelledPreview.ReplyPreviewId, "test-operator",
            PreviewConfirmationService.ReplySha256(cancelledPreview.Reply), adapter);
        check(cancelled.Status == ReplyDeliveryStatuses.Cancelled && afterCancel.Status == "PREVIEW_CANCELLED" && adapter.Calls == 1,
            "cancelled preview cannot be sent");
        var tampered = Create("原始回复");
        service.Show(tampered.ReplyPreviewId);
        var tamperedFolder = Directory.EnumerateDirectories(Path.Combine(root, "outbox"))
            .Single(path => JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(path, "reply-preview.json")))?.ReplyPreviewId == tampered.ReplyPreviewId);
        File.WriteAllText(Path.Combine(tamperedFolder, "reply-preview.json"), JsonSerializer.Serialize(tampered with { Reply = "被改动的回复" }));
        var tamperedOutcome = await service.ConfirmSendAsync(tampered.ReplyPreviewId, "test-operator",
            PreviewConfirmationService.ReplySha256(tampered.Reply), adapter);
        check(tamperedOutcome.Status == "PREVIEW_TAMPERED" && adapter.Calls == 1,
            "changed preview text is rejected even when an earlier review hash is supplied");

        var port = new ProbePort();
        var native = new QnbotSendAdapter(port, new VerifiedQnbotSendTarget(preview.SessionId, preview.BuyerId,
            "seller", "buyer", true));
        var nativeResult = await native.SendAsync(request, CancellationToken.None);
        check(nativeResult.Status == "IDENTITY_UNVERIFIED" && port.Calls == 0,
            "qnbot send adapter refuses synthetic OCR buyer identity before invoking a transport");
        var verifiedRequest = request with { BuyerId = "verified-platform-buyer" };
        var verifiedPort = new ProbePort();
        var verifiedIdentity = new BuyerIdentity(verifiedRequest.BuyerId, BuyerIdentitySafety.TrustedSource, 1.0,
            DateTimeOffset.Now, request.SessionId, BuyerIdentityStatuses.Verified, "buyer",
            "conversation-fixture", "evidence-fixture", false, DateTimeOffset.Now);
        var verifiedAdapter = new QnbotSendAdapter(verifiedPort,
            new VerifiedQnbotSendTarget(request.SessionId, verifiedRequest.BuyerId, "seller", "buyer", true), verifiedIdentity);
        var verifiedResult = await verifiedAdapter.SendAsync(verifiedRequest, CancellationToken.None);
        check(verifiedResult.Status == "QnbotAccepted" && verifiedResult.Acknowledged && verifiedPort.Calls == 1,
            "verified target delegates once to an injected qnbot send port (synthetic fixture only)");
        var events = File.ReadAllLines(Path.Combine(root, "send-lifecycle.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("Event").GetString()).ToArray();
        check(new[] { "PREVIEW_CREATED", "WAITING_CONFIRMATION", "PREVIEW_NOT_FOUND", "CONFIRMED", "SEND_REQUEST",
            "SEND_RESULT", "DUPLICATE_CONFIRM", "SEND_FAILED", "CANCELLED", "PREVIEW_TAMPERED" }.All(events.Contains),
            "send lifecycle audit records creation, explicit confirmation, result and all required failures");
    }
}
