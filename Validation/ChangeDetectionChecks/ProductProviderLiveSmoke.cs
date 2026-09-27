using System.Text.Json;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ProductProviderLiveSmoke
{
    public static async Task<int> RunAsync(string providerConfigPath)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV117", "provider-live_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
        var state = Path.Combine(root, "state"); Directory.CreateDirectory(root);
        var sessionId = new string('A', 64);
        var identity = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var productId = "1051087533910";
        var captureId = "synthetic-product-fixture:frame_000001";
        var captured = DateTimeOffset.Now;
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "synthetic-product-buyer",
            "IsolatedTestIdentity", productId, identity, "synthetic-product-buyer", captured, captured,
            [new ProductReference(productId, "SyntheticCurrentTest", captured, captureId, "ObservedNotVerified")]);
        var question = new BuyerMessage { Text = "这款有200ml规格吗？", Timestamp = new DateTime(2026, 9, 26, 12, 0, 0) };
        var envelope = new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, productId,
            [new("buyer", question.Text, question.Timestamp)]) { ProductBinding = "SyntheticProductKnowledgeValidation" };
        var messageId = MessageFingerprint.StableId(sessionId, question);
        var item = new DispatchItem(messageId, MessageFingerprint.Create(question), session, envelope,
            captured, true, CaptureId: captureId, AiRequestId: MessageFingerprint.AiRequestId(messageId));
        var context = SessionContextBuilder.Build(item, Array.Empty<DispatchItem>());
        var trace = new AgentPipelineTrace(root);
        if (!trace.MessageCaptured(item)) throw new IOException("Agent audit unavailable; no AI request started.");
        using var dispatch = new DurableShadowDispatch(state, Path.Combine(root, "lifecycle.jsonl"), CancellationToken.None,
            new AgentShadowClient(providerConfigPath).ConvertAsync, true, trace,
            knowledgeProvider: new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(
                Path.Combine("Knowledge", "product-knowledge.json"))),
            enrichAiContext: true);
        if (!dispatch.Submit(item, context)) throw new InvalidOperationException("Synthetic fixture unexpectedly duplicated.");
        dispatch.Start(); await dispatch.StopAsync();
        var final = dispatch.Snapshot().Single();
        var folder = Path.Combine(state, "outbox", messageId);
        var previewPath = Path.Combine(folder, "reply-preview.json");
        var receiptPath = Path.Combine(folder, "attempt_0001", "shadow-adapter-result.json");
        using var receipt = File.Exists(receiptPath) ? JsonDocument.Parse(File.ReadAllText(receiptPath)) : null;
        var calls = receipt?.RootElement.GetProperty("AiCalls").GetInt32() ?? 0;
        var sent = receipt?.RootElement.GetProperty("SentMessages").GetInt32() ?? 0;
        var preview = File.Exists(previewPath) ? JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(previewPath)) : null;
        var grounded = preview?.Reply.Contains("200ml", StringComparison.OrdinalIgnoreCase) == true;
        var result = new { Mode = "SyntheticProductKnowledgeProviderSmoke", State = final.State,
            AiCalls = calls, ReplyPreviewCount = preview is null ? 0 : 1, SentMessages = sent,
            KnowledgeStatus = preview?.KnowledgeStatus, KnowledgeSource = preview?.KnowledgeSource,
            KnowledgeUsedByAi = preview?.KnowledgeUsedByAi, ReplyMentions200ml = grounded,
            ProductId = context.ProductId, Evidence = root, Preview = previewPath, Receipt = receiptPath,
            SendEnabled = false, ApiKeyPrinted = false };
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
        return final.State == "Completed" && calls > 0 && sent == 0 && preview is not null &&
            preview.KnowledgeStatus == KnowledgeStatuses.Available && preview.KnowledgeUsedByAi && grounded ? 0 : 3;
    }
}
