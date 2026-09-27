using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Product;
using QnocrPoc.Shadow;

internal static class ProductContextBindingChecks
{
    private sealed class CountingKnowledge : IKnowledgeProvider
    {
        public int Calls { get; private set; }
        public Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new Exception("Knowledge source must not be queried without current product context.");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV129", "binding_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var mapping = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var registry = new SessionRegistry(root, "v129-fixture", mapping);
        var manual = new ManualProductResolver(root);
        var ocr = new OcrProductResolver();
        var resolver = new SessionProductContextResolver(manual, ocr);
        var at = DateTimeOffset.UtcNow;
        var productA = "1051087533910"; var productB = "1111111111111";
        var initial = registry.Resolve("buyer-A", "", at, "capture-manual");
        var bound = manual.Bind(initial.SessionId, initial.SellerId, productA,
            "人工核对的测试商品", "operator-test", at);
        check(bound.ProductId == productA && bound.Source == ProductContextSource.Manual &&
            bound.Confidence is null && bound.BoundBy == "operator-test",
            "manual binding requires an existing seller-scoped session and stores explicit attribution");
        try { manual.Bind(initial.SessionId, "seller-other", productA, "错误店铺商品", "operator-test", at);
            throw new Exception("cross-seller manual bind accepted"); }
        catch (InvalidDataException) { check(true, "manual binding rejects a SellerId different from the persisted session"); }
        try { manual.Bind(initial.SessionId, initial.SellerId, productB, "另一个商品", "operator-test", at);
            throw new Exception("silent manual product replacement accepted"); }
        catch (InvalidOperationException) { check(true, "changing an active manual product requires an explicit clear"); }
        check(manual.Resolve(initial, null, at.AddHours(3), "capture-stale") is null,
            "expired manual binding cannot silently become current context after long inactivity");
        var resolution = resolver.Resolve(initial, null, at, "capture-manual");
        var session = registry.BindCurrentProduct(initial, resolution, at, "capture-manual");
        check(resolution.Status == ProductContextBindingStatus.Bound &&
            session.CurrentProductContext?.ProductId == productA && session.ProductId == "" &&
            session.ProductReferences?.Single().ProductId == productA,
            "manual current product binds to Session and ProductReference without editing OCR ProductId");
        var persisted = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(Path.Combine(root,
            "sessions", session.SessionId, "session.json")))!;
        check(persisted.CurrentProductContext?.ProductId == productA &&
            persisted.ProductContextBindingStatus == ProductContextBindingStatus.Bound &&
            File.ReadAllText(Path.Combine(root, "product-context-audit.jsonl")).Contains("PRODUCT_CONTEXT_BOUND"),
            "current binding survives restart and writes PRODUCT_CONTEXT_BOUND audit");
        var context = Context(session, "这款有200ml吗？", new string('1', 64), "capture-manual");
        var decision = new RuleBasedIntentClassifier().Classify(context);
        var request = KnowledgeRequestBuilder.Build(context, decision, session.SellerId);
        check(context.ProductId == productA && context.CurrentProductContext?.Source == ProductContextSource.Manual &&
            request.ProductReference?.ProductId == productA && request.SellerId == "seller-test" &&
            request.BindingStatus == ProductContextBindingStatus.Bound && request.NeedFields.SequenceEqual(["Capacity"]),
            "KnowledgeRequest prefers the explicit current session context over empty MessageEnvelope ProductId");
        var provider = new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(
            Path.Combine("Knowledge", "product-knowledge.json")));
        var knowledge = await provider.GetAsync(request);
        check(knowledge.Success && knowledge.ReturnedFields?["Capacity"].Contains("200ml") == true,
            "bound test product A resolves its published Capacity without guessing another product");
        var run = Path.Combine(root, "run"); var state = Path.Combine(root, "dispatch");
        var dispatchItem = Item(session, context, true);
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
            (_, _, attempt, _) =>
            {
                aiCalls++;
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                var k = model.RootElement.GetProperty("Knowledge");
                check(k.GetProperty("FactFields").GetProperty("Capacity").GetString() == "100ml, 200ml" &&
                    model.RootElement.GetProperty("CurrentProductContext").GetProperty("ProductId").GetString() == productA,
                    "AI sidecar contains only requested bound Capacity and current product ID");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试商品有200ml规格。", 30, DateTimeOffset.UtcNow, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(dispatchItem, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", dispatchItem.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.KnowledgeStatus == KnowledgeStatuses.Available &&
            preview.KnowledgeUsedByAi && preview.SendStatus == "Shadow" && preview.Reply.Contains("200ml"),
            "manual binding reaches one synthetic AI reply preview with KnowledgeUsedByAi=true and no send");
        var hostAvailable = HostContextCheck(Path.Combine(state, "outbox", dispatchItem.MessageId),
            session.IdentityPath!, Path.Combine(root, "host-available"));
        check(hostAvailable.GetProperty("KnowledgeFactsPresent").GetBoolean() &&
            hostAvailable.GetProperty("KnowledgeStatus").GetString() == "Available" &&
            hostAvailable.GetProperty("AiCalls").GetInt32() == 0 &&
            hostAvailable.GetProperty("SentMessages").GetInt32() == 0,
            "isolated original qnbot host accepts bound sidecar knowledge with unchanged empty-ProductId envelope");
        OcrResult Evidence(params (string Text, float Score)[] lines) => new("product-fixture", "Synthetic",
            lines.Select((line, index) => new OcrLine(index + 1, line.Text, line.Score,
                new OcrBounds(10, 10 + index * 32, 350, 25))).ToArray(), 0, .99, 0, 0, 0, 0, 0, 0);
        var same = resolver.Resolve(session, Evidence(("商品ID：" + productA, .99f),
            ("商品名称：测试商品", .99f)), at.AddSeconds(1), "capture-same");
        check(same.Status == ProductContextBindingStatus.Bound && same.Current?.ProductId == productA,
            "same-capture OCR agreeing with manual product keeps one active binding");
        var ocrOnlySession = registry.Resolve("buyer-B", "", at.AddSeconds(2), "capture-ocr");
        var ocrOnly = resolver.Resolve(ocrOnlySession, Evidence(("商品ID：" + productA, .99f),
            ("商品名称：OCR测试商品", .99f)), at.AddSeconds(2), "capture-ocr");
        check(ocrOnly.Status == ProductContextBindingStatus.Bound &&
            ocrOnly.Current?.Source == ProductContextSource.OCR && ocrOnly.Current.ProductId == productA &&
            ocrOnly.Current.ProductName == "OCR测试商品" && ocrOnly.Current.CaptureId == "capture-ocr",
            "unique high-confidence labeled OCR ID and name bind only in the current capture");
        var low = resolver.Resolve(session, Evidence(("商品ID：" + productA, .4f)),
            at.AddSeconds(3), "capture-low");
        var ambiguous = resolver.Resolve(session, Evidence(("商品ID：" + productA, .99f),
            ("商品ID：" + productB, .99f)), at.AddSeconds(4), "capture-ambiguous");
        check(low.Status == ProductContextBindingStatus.Uncertain && low.Current is null &&
            ambiguous.Status == ProductContextBindingStatus.Uncertain && ambiguous.Current is null,
            "low-confidence or ambiguous current OCR blocks even an older manual choice");
        var changed = registry.Resolve("buyer-A", productB, at.AddSeconds(5), "capture-conflict");
        var conflict = resolver.Resolve(changed, Evidence(("商品ID：" + productB, .99f)),
            at.AddSeconds(5), "capture-conflict");
        var conflicted = registry.BindCurrentProduct(changed, conflict, at.AddSeconds(5), "capture-conflict");
        var conflictContext = Context(conflicted, "这款有200ml吗？", new string('2', 64), "capture-conflict");
        var conflictRequest = KnowledgeRequestBuilder.Build(conflictContext,
            new RuleBasedIntentClassifier().Classify(conflictContext), conflicted.SellerId);
        check(conflict.Status == ProductContextBindingStatus.Conflict && conflict.Current is null &&
            conflictRequest.ProductReference is null && conflictRequest.MissingProductContext &&
            conflicted.ProductReferences?.Any(reference => reference.ProductId == productA) == true,
            "manual A vs visible OCR B is Conflict; historical A remains audit only, never a lookup fallback");
        var noCurrent = registry.Resolve("buyer-C", productB, at.AddSeconds(6), "capture-old");
        noCurrent = registry.Resolve("buyer-C", "", at.AddSeconds(7), "capture-unknown");
        noCurrent = registry.BindCurrentProduct(noCurrent, resolver.Resolve(noCurrent, null,
            at.AddSeconds(7), "capture-unknown"), at.AddSeconds(7), "capture-unknown");
        var unknownContext = Context(noCurrent, "这款有200ml吗？", new string('3', 64), "capture-unknown");
        var unknownRequest = KnowledgeRequestBuilder.Build(unknownContext,
            new RuleBasedIntentClassifier().Classify(unknownContext), noCurrent.SellerId);
        check(noCurrent.ProductReferences?.Any(reference => reference.ProductId == productB) == true &&
            unknownRequest.ProductReference is null && unknownRequest.MissingProductContext,
            "no current context returns MissingProductContext despite a valid historical ProductId B");
        var counting = new CountingKnowledge();
        var unknownRun = Path.Combine(root, "unknown-run"); var unknownState = Path.Combine(root, "unknown-dispatch");
        var unknownItem = Item(noCurrent, unknownContext, true);
        using (var dispatch = new DurableShadowDispatch(unknownState, Path.Combine(unknownRun, "lifecycle.jsonl"),
            CancellationToken.None, (_, _, attempt, _) =>
            {
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                check(model.RootElement.GetProperty("Knowledge").GetProperty("Status").GetString() ==
                    KnowledgeStatuses.MissingProductContext &&
                    model.RootElement.GetProperty("Knowledge").GetProperty("FactFields").EnumerateObject().Count() == 0,
                    "missing context model sidecar contains no product facts");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "需要人工确认当前商品。", 20, DateTimeOffset.UtcNow, 1));
            }, true, new AgentPipelineTrace(unknownRun), knowledgeProvider: counting, enrichAiContext: true))
        {
            dispatch.Submit(unknownItem, unknownContext); dispatch.Start(); await dispatch.StopAsync();
        }
        var unknownPreview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(unknownState,
            "outbox", unknownItem.MessageId, "reply-preview.json")))!;
        check(counting.Calls == 0 && unknownPreview.KnowledgeStatus == KnowledgeStatuses.MissingProductContext &&
            !unknownPreview.KnowledgeUsedByAi && unknownPreview.KnowledgeGuardApplied,
            "no current context skips provider lookup and produces human-review MissingProductContext preview");
        var hostMissing = HostContextCheck(Path.Combine(unknownState, "outbox", unknownItem.MessageId),
            noCurrent.IdentityPath!, Path.Combine(root, "host-missing"));
        check(hostMissing.GetProperty("KnowledgeStatus").GetString() == KnowledgeStatuses.MissingProductContext &&
            !hostMissing.GetProperty("KnowledgeFactsPresent").GetBoolean() &&
            hostMissing.GetProperty("SentMessages").GetInt32() == 0,
            "isolated qnbot host accepts missing context sidecar without product facts or send");
        manual.Clear(session.SessionId, "operator-test");
        check(manual.Resolve(session, null, at.AddSeconds(8), "capture-cleared") is null &&
            File.ReadAllText(Path.Combine(root, "product-context-audit.jsonl")).Contains("PRODUCT_CONTEXT_CLEARED"),
            "operator clear removes the active manual choice without deleting historical audit");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "product context binding creates no send request");
    }

    private static JsonElement HostContextCheck(string outboxFolder, string identityPath, string output)
    {
        var host = Path.GetFullPath(Path.Combine("Validation", "QnbotCompatibility", "bin", "Debug", "net48",
            "QnbotCompatibility.exe"));
        var start = new System.Diagnostics.ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--agent-context-check", Path.Combine(outboxFolder, "envelope.json"),
            identityPath, output, Path.Combine(outboxFolder, "attempt_0001", "model-context.json") })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new IOException("Isolated compatibility host did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
            throw new InvalidDataException("Isolated context check failed: " + stderr + stdout);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "context-check.json")));
        return receipt.RootElement.Clone();
    }
    private static SessionContextView Context(MessageSession session, string text, string messageId,
        string captureId)
    {
        var item = new DispatchItem(messageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, session.ProductId,
                [new EnvelopeMessage("buyer", text, new DateTime(2026, 9, 27, 12, 0, 0))]),
            DateTimeOffset.UtcNow, true, CaptureId: captureId, AiRequestId: new string('C', 64));
        return SessionContextBuilder.Build(item, []);
    }
    private static DispatchItem Item(MessageSession session, SessionContextView context, bool ai) =>
        new(context.MessageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, session.ProductId,
                [context.Messages[^1]]), DateTimeOffset.UtcNow, ai,
            CaptureId: context.CaptureId, AiRequestId: context.AiRequestId);
}
