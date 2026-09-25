using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ProviderLiveSmoke
{
    public static async Task<int> RunAsync(string providerConfigPath)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV18", "provider-smoke_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        var sessionId = new string('A', 64);
        var identity = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var capturedAt = DateTimeOffset.Now;
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "synthetic-fixture",
            "IsolatedTestIdentity", "", identity, "synthetic-fixture", capturedAt, capturedAt,
            [new ProductReference("1051087533910", "SyntheticFixture", capturedAt,
                "synthetic-provider-fixture:frame_000000", "HistoricalUnverified")]);
        var question = new BuyerMessage { Text = "你好，请回复一句简短的测试问候。",
            Timestamp = new DateTime(2026, 9, 25, 21, 0, 0) };
        var envelope = new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, "",
            [new("buyer", question.Text, question.Timestamp)]) { ProductBinding = "SyntheticProviderValidation" };
        var messageId = MessageFingerprint.StableId(sessionId, question);
        var item = new DispatchItem(messageId, MessageFingerprint.Create(question), session, envelope,
            capturedAt, true, CaptureId: "synthetic-provider-fixture:frame_000001",
            AiRequestId: MessageFingerprint.AiRequestId(messageId));
        var context = SessionContextBuilder.Build(item, Array.Empty<DispatchItem>());
        var trace = new AgentPipelineTrace(root);
        if (!trace.MessageCaptured(item)) throw new IOException("Agent audit unavailable; no AI request started.");
        using var dispatch = new DurableShadowDispatch(state, Path.Combine(root, "lifecycle.jsonl"), CancellationToken.None,
            new AgentShadowClient(providerConfigPath).ConvertAsync, true, trace);
        if (!dispatch.Submit(item, context)) throw new InvalidOperationException("Synthetic fixture unexpectedly duplicated.");
        dispatch.Start();
        await dispatch.StopAsync();
        var final = dispatch.Snapshot().Single();
        var folder = Path.Combine(state, "outbox", messageId);
        var previewPath = Path.Combine(folder, "reply-preview.json");
        var receiptPath = Path.Combine(folder, "attempt_0001", "shadow-adapter-result.json");
        using var receipt = File.Exists(receiptPath) ? JsonDocument.Parse(File.ReadAllText(receiptPath)) : null;
        var actualCalls = receipt?.RootElement.GetProperty("AiCalls").GetInt32() ?? 0;
        var sendCount = receipt?.RootElement.GetProperty("SentMessages").GetInt32() ?? 0;
        var preview = File.Exists(previewPath) ? JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(previewPath)) : null;
        var result = new { Mode = "ProviderPipelineSyntheticSmoke", Status = final.State,
            ProviderConfig = Path.GetFullPath(providerConfigPath), AiCalls = actualCalls,
            ReplyPreviewCount = preview is null ? 0 : 1, SentMessages = sendCount,
            ProductReferenceCount = context.ProductReferences?.Count ?? 0,
            CurrentProductId = context.ProductId, MessageLinkCount = context.MessageProductLinks?.Count ?? 0,
            SendEnabled = false, Preview = previewPath, Receipt = receiptPath, Evidence = root,
            Reply = preview?.Reply };
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(result));
        Console.WriteLine(JsonSerializer.Serialize(result));
        return final.State == "Completed" && actualCalls > 0 && preview is not null && sendCount == 0 ? 0 : 3;
    }
}
