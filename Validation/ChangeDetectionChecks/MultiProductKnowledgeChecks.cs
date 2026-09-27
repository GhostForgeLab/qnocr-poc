using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class MultiProductKnowledgeChecks
{
    private sealed class WrongScopeRepository : IProductKnowledgeRepository
    {
        public Task<ProductKnowledgeRecord?> GetAsync(string sellerId, string productId, CancellationToken token = default) =>
            Task.FromResult<ProductKnowledgeRecord?>(new ProductKnowledgeRecord(productId,
                new Dictionary<string, string> { ["Capacity"] = "500ml" }, "LocalTestProductConfig",
                DateTimeOffset.Now, true, "seller-other", "wrong-scope"));
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        var source = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.multi.sample.json"));
        IProductKnowledgeRepository repository = new JsonProductKnowledgeRepository(source);
        var a = await repository.GetAsync("seller-test", "1051087533910");
        var b = await repository.GetAsync("seller-test", "2000000000001");
        var otherStore = await repository.GetAsync("seller-other", "1051087533910");
        check(a?.Fields["Capacity"] == "100ml" && a.Version == "test-v1" && a.SellerId == "seller-test" &&
            b?.Fields["Capacity"] == "200ml" && b.Version == "test-v2",
            "repository resolves two ProductIds to their own versioned local test records");
        check(otherStore?.Fields["Capacity"] == "500ml" && otherStore.Version == "other-store-test-v1" &&
            a!.Fields["Capacity"] != otherStore.Fields["Capacity"],
            "the same ProductId in another store cannot reuse seller-test facts");
        a!.Fields["Capacity"] = "tampered";
        check((await repository.GetAsync("seller-test", "1051087533910"))?.Fields["Capacity"] == "100ml",
            "repository returns a defensive record copy instead of mutable shared facts");
        check(await repository.GetAsync("seller-test", "3000000000001") is null &&
            await repository.GetAsync("unknown-seller", "1051087533910") is null,
            "missing product or seller is an exact miss without cross-product fallback");
        var provider = new ProductKnowledgeProvider(repository);
        var classifier = new RuleBasedIntentClassifier();
        var sessionId = new string('A', 64);
        SessionContextView Context(string productId, string question, string messageId) => new(sessionId, "buyer-test", productId,
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "multi-product-fixture:frame_000001", messageId, new string('C', 64), 1);
        async Task<KnowledgeResult> Capacity(string seller, string productId, string messageId)
        {
            var context = Context(productId, "这款容量是多少？", messageId);
            return await provider.GetAsync(KnowledgeRequestBuilder.Build(context, classifier.Classify(context), seller));
        }
        var wrongScopeContext = Context("1051087533910", "容量是多少？", new string('9', 64));
        try { await new ProductKnowledgeProvider(new WrongScopeRepository()).GetAsync(
                KnowledgeRequestBuilder.Build(wrongScopeContext, classifier.Classify(wrongScopeContext), "seller-test"));
            throw new Exception("cross-seller repository record accepted"); }
        catch (InvalidDataException) { check(true, "provider rejects a repository record from another seller scope"); }
        var resultA = await Capacity("seller-test", "1051087533910", new string('1', 64));
        var resultB = await Capacity("seller-test", "2000000000001", new string('2', 64));
        var resultOther = await Capacity("seller-other", "1051087533910", new string('3', 64));
        check(resultA.Success && resultA.ReturnedFields?["Capacity"] == "100ml" &&
            resultA.ProductVersion == "test-v1" && resultA.Source == "LocalTestProductConfig" &&
            resultA.UpdatedAt == a!.UpdatedAt &&
            resultB.Success && resultB.ReturnedFields?["Capacity"] == "200ml" && resultB.ProductVersion == "test-v2",
            "ProductKnowledgeProvider remains repository-agnostic and returns A/B facts with correct versions");
        check(resultOther.Success && resultOther.ReturnedFields?["Capacity"] == "500ml" &&
            resultOther.SellerId == "seller-other" && resultA.SellerId == "seller-test",
            "provider preserves seller scope even when ProductIds are identical");
        var missing = await Capacity("seller-test", "3000000000001", new string('4', 64));
        check(!missing.Success && missing.MissingProduct && missing.Source == "None" &&
            missing.ProductVersion is null && missing.ReturnedFields is null &&
            missing.MissingFields.SequenceEqual(["Capacity"]),
            "Product C returns MissingProduct=true and never borrows Product A or B content");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV119", "multi_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var duplicatePath = Path.Combine(root, "duplicate.json");
        File.WriteAllText(duplicatePath, JsonSerializer.Serialize(new { Products = new[] { b, b } }));
        try { _ = new JsonProductKnowledgeRepository(duplicatePath); throw new Exception("duplicate scope accepted"); }
        catch (InvalidDataException) { check(true, "duplicate SellerId/ProductId keys are rejected at load time"); }

        async Task<ReplyPreview> Dispatch(string name, string seller, string productId, string messageId,
            string fakeReply, Action<JsonElement> inspect)
        {
            var context = Context(productId, "这款容量是多少？", messageId);
            var run = Path.Combine(root, name + "-run"); var state = Path.Combine(root, name + "-state");
            var session = new MessageSession(sessionId, seller, "buyer-test", "fixture", "Test", productId, "test-mapping.json");
            var item = new DispatchItem(messageId, new string('D', 64), session,
                new MessageEnvelope("Qianniu.OCR", seller, session.BuyerId, productId, [context.Messages[^1]]),
                DateTimeOffset.Now, true, CaptureId: context.CaptureId, AiRequestId: context.AiRequestId);
            var trace = new AgentPipelineTrace(run);
            var calls = 0;
            using var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
                (_, _, attempt, _) =>
                {
                    calls++;
                    using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                    inspect(model.RootElement.GetProperty("Knowledge"));
                    return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                        fakeReply, 40, DateTimeOffset.Now, 1));
                }, true, trace, knowledgeProvider: provider, enrichAiContext: true);
            dispatch.Submit(item, context); dispatch.Start(); await dispatch.StopAsync();
            check(calls == 1 && dispatch.Snapshot().Single().State == "Completed" && dispatch.Snapshot().Single().AiCalls == 1,
                "scoped knowledge metadata reaches unchanged synthetic AI callback once with Send OFF");
            var folder = Path.Combine(state, "outbox", messageId);
            return JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(folder, "reply-preview.json")))!;
        }
        var previewB = await Dispatch("product-b", "seller-test", "2000000000001", new string('5', 64),
            "这款容量是200ml。", knowledge =>
            {
                check(knowledge.GetProperty("SellerId").GetString() == "seller-test" &&
                    knowledge.GetProperty("ProductVersion").GetString() == "test-v2" &&
                    knowledge.GetProperty("FactFields").GetProperty("Capacity").GetString() == "200ml",
                    "model context carries only Product B's store-scoped versioned fact");
            });
        check(previewB.KnowledgeStatus == "Available" && previewB.ProductKnowledgeVersion == "test-v2" &&
            !previewB.MissingProduct && previewB.KnowledgeUsedByAi && previewB.Reply.Contains("200ml"),
            "Product B preview reports the correct fact source/version and a grounded synthetic answer");
        var productBView = new AssistantReplyService(Path.Combine(root, "product-b-state")).Show(previewB.ReplyPreviewId);
        check(productBView?.CurrentProductId == "2000000000001" && productBView.ProductKnowledgeVersion == "test-v2",
            "human assistant view identifies the current Product B independently of historical reference lists");
        var previewC = await Dispatch("missing-c", "seller-test", "3000000000001", new string('6', 64),
            "其实有200ml。", knowledge =>
            {
                check(knowledge.GetProperty("Status").GetString() == "Missing" &&
                    knowledge.GetProperty("MissingProduct").GetBoolean() &&
                    knowledge.GetProperty("FactFields").EnumerateObject().Count() == 0,
                    "nonexistent Product C supplies no A/B facts to model context");
            });
        check(previewC.KnowledgeStatus == "Missing" && previewC.MissingProduct &&
            previewC.KnowledgeGuardApplied && !previewC.Reply.Contains("200ml") && previewC.Reply.Contains("人工核实"),
            "nonexistent Product C fails closed in the human preview despite a fake cross-product AI claim");
        var events = File.ReadAllLines(Path.Combine(root, "product-b-run", "agent-pipeline.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var requested = events.Single(e => e.GetProperty("Event").GetString() == "KNOWLEDGE_REQUEST");
        var resolved = events.Single(e => e.GetProperty("Event").GetString() == "KNOWLEDGE_RESULT");
        check(requested.GetProperty("SellerId").GetString() == "seller-test" &&
            requested.GetProperty("ProductReference").GetString() == "2000000000001" &&
            resolved.GetProperty("ProductVersion").GetString() == "test-v2" &&
            resolved.GetProperty("Source").GetString() == "LocalTestProductConfig" &&
            resolved.GetProperty("MissingProduct").GetBoolean() == false,
            "knowledge trace records seller, product, version, source and missing-product status without fact values");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "multi-product PoC creates no send request");
    }
}
