using QnocrPoc.Extension;
using QnocrPoc.Models;

internal static class AgentExtensionChecks
{
    private sealed class RecordingExtension : IAgentExtension
    {
        public bool Initialized { get; private set; }
        public int Calls { get; private set; }
        public MessageEnvelope? Envelope { get; private set; }
        public AgentExtensionEvidence? Evidence { get; private set; }
        public bool ThrowAfterObserve { get; set; }
        public bool UnsafeReceipt { get; set; }
        public bool IgnorePreviewCorrelation { get; set; }
        public ReplyPreview? Preview { get; set; }
        public void Initialize(AgentExtensionOptions options)
        {
            if (!options.ShadowOnly || options.SendEnabled) throw new InvalidDataException("Unsafe extension mode.");
            Initialized = true;
        }
        public Task<AgentExtensionReceipt> ProcessMessageAsync(MessageEnvelope envelope,
            AgentExtensionEvidence evidence, CancellationToken cancellationToken = default)
        {
            Calls++; Envelope = envelope; Evidence = evidence;
            if (ThrowAfterObserve) throw new IOException("Synthetic observation uncertainty.");
            return Task.FromResult(new AgentExtensionReceipt("RecordedForShadow", evidence.CorrelationId,
                Preview?.ReplyPreviewId, UnsafeReceipt, UnsafeReceipt ? 1 : 0));
        }
        public ReplyPreview? GetReplyPreview(string correlationId) =>
            IgnorePreviewCorrelation || correlationId == Evidence?.CorrelationId ? Preview : null;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var extension = new RecordingExtension();
        var adapter = new QnbotAgentAdapter(extension);
        var at = new DateTime(2026, 9, 27, 12, 0, 0);
        var observed = new QnbotInboundObservation("seller-test", "buyer-test:fixture",
            "seller-fixture", "buyer-fixture", "buyer", "请问这款有200ml吗？", at,
            "native-message-1", "test-conversation", "test-target", "IsolatedTest", "fixture-evidence");
        try { await adapter.ObserveAsync(observed); throw new Exception("uninitialized extension accepted"); }
        catch (InvalidOperationException) { check(true, "adapter requires explicit Shadow-only extension initialization"); }
        adapter.Initialize();
        check(extension.Initialized, "extension receives a no-send initialization contract");
        var receipt = await adapter.ObserveAsync(observed);
        check(receipt.Status == "RecordedForShadow" && !receipt.SendEnabled && receipt.SentMessages == 0 &&
            extension.Calls == 1 && extension.Envelope is { SchemaVersion: 1, ProductId: "" } &&
            extension.Envelope.Source == "Qnbot.Native.ReadOnly" &&
            extension.Envelope.Messages.Single().Role == "buyer" &&
            extension.Envelope.Messages.Single().Text == observed.Text &&
            extension.Evidence?.NativeMessageId == observed.NativeMessageId,
            "read-only qnbot observation crosses the adapter as unchanged MessageEnvelope v1 plus native evidence");
        var duplicate = await adapter.ObserveAsync(observed);
        check(duplicate.Status == "Duplicate" && extension.Calls == 1,
            "same native message is not processed twice in one extension instance");
        var seller = await adapter.ObserveAsync(observed with { NativeMessageId = "native-seller", Role = "seller" });
        var unknown = await adapter.ObserveAsync(observed with { NativeMessageId = "native-unknown", IdentityStatus = "Unverified" });
        var noId = await adapter.ObserveAsync(observed with { NativeMessageId = "" });
        check(seller.Status == "Held" && seller.Reason == "NotBuyer" &&
            unknown.Status == "Held" && unknown.Reason == "IdentityUnverified" &&
            noId.Status == "Held" && noId.Reason == "NativeMessageIdMissing" && extension.Calls == 1,
            "seller echo, unverified identity and missing native message ID cannot enter the Agent extension");
        var weakVerified = await adapter.ObserveAsync(observed with { NativeMessageId = "native-verified",
            IdentityStatus = "Verified", ConversationCode = "", NativeBuyerTargetId = "" });
        check(weakVerified.Status == "Held" && weakVerified.Reason == "NativeIdentityEvidenceMissing",
            "a Verified label without native target/conversation evidence is held");
        var context = new SessionContextView(new string('A', 64), observed.BuyerId, "",
            [new EnvelopeMessage("buyer", observed.Text, at)], DateTimeOffset.UtcNow,
            "fixture:capture", new string('1', 64), new string('C', 64), 1);
        extension.Preview = new ReplyPreview(context.SessionId, context.BuyerId, context,
            "仅供人工查看的测试预览", null, DateTimeOffset.UtcNow, "Shadow", context.CaptureId,
            context.AiRequestId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        extension.IgnorePreviewCorrelation = true;
        check(adapter.GetReplyPreview(receipt.CorrelationId)?.Reply == extension.Preview.Reply &&
            adapter.GetReplyPreview("unseen") is null,
            "GetReplyPreview exposes only an already-created Shadow preview, never generates or sends one");
        extension.ThrowAfterObserve = true;
        var uncertain = await adapter.ObserveAsync(observed with { NativeMessageId = "native-failed" });
        var replay = await adapter.ObserveAsync(observed with { NativeMessageId = "native-failed" });
        check(uncertain.Status == "Uncertain" && replay.Status == "Duplicate" && extension.Calls == 2,
            "extension exception is held as uncertain and never auto-replayed into the qnbot message chain");
        extension.ThrowAfterObserve = false; extension.UnsafeReceipt = true;
        var unsafeResult = await adapter.ObserveAsync(observed with { NativeMessageId = "native-unsafe" });
        check(unsafeResult.Status == "Uncertain" && unsafeResult.SentMessages == 0 &&
            extension.Calls == 3,
            "unsafe extension receipt is rejected without claiming a send occurred");
        extension.Preview = extension.Preview with { SendStatus = "Sent" };
        try { adapter.GetReplyPreview(unsafeResult.CorrelationId); throw new Exception("sent preview exposed"); }
        catch (InvalidDataException) { check(true, "non-Shadow reply preview cannot cross the extension boundary"); }
    }
}
