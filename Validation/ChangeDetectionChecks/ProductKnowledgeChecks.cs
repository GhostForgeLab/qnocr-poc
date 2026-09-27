using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ProductKnowledgeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var source = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.json"));
        IKnowledgeProvider provider = new ProductKnowledgeProvider(new JsonProductKnowledgeRepository(source));
        var sid = new string('A', 64); var id = "1051087533910";
        SessionContextView Context(string question, string productId, string messageId) => new(sid, "buyer-test", productId,
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 26, 12, 0, 0))],
            DateTimeOffset.Now, "product-fixture:frame_000001", messageId, new string('C', 64), 1);
        var classifier = new RuleBasedIntentClassifier();
        var specContext = Context("这款有200ml规格吗？", id, new string('1', 64));
        var specDecision = classifier.Classify(specContext);
        var specRequest = KnowledgeRequestBuilder.Build(specContext, specDecision, "seller-test");
        var spec = await provider.GetAsync(specRequest);
        check(specDecision.IntentName == "PRODUCT_SPEC_QUERY" && specRequest.NeedFields.SequenceEqual(["Capacity"]) &&
            spec.Success && spec.Source == "LocalTestProductConfig" && spec.ReturnedFields?["Capacity"].Contains("200ml") == true &&
            spec.Content.Contains("200ml") && spec.MissingFields.Count == 0 && spec.Confidence is null,
            "one local test product supplies the requested 200ml Capacity fact without invented confidence");
        var materialContext = Context("什么材质？", id, new string('2', 64));
        var material = await provider.GetAsync(KnowledgeRequestBuilder.Build(materialContext, classifier.Classify(materialContext), "seller-test"));
        check(material.Success && material.ReturnedFields?["Material"] == "TA1纯钛" &&
            material.MissingFields.Count == 0,
            "material query returns only the local TA1 pure-titanium test field");
        var waterproofContext = Context("防水等级是多少？", id, new string('3', 64));
        var waterproofDecision = classifier.Classify(waterproofContext);
        var waterproofRequest = KnowledgeRequestBuilder.Build(waterproofContext, waterproofDecision, "seller-test");
        var waterproof = await provider.GetAsync(waterproofRequest);
        check(waterproofDecision.IntentName == "OTHER" && waterproofRequest.NeedFields.SequenceEqual(["Waterproof"]) &&
            !waterproof.Success && waterproof.MissingFields.SequenceEqual(["Waterproof"]) && waterproof.Content == "",
            "knowledge field detection requests Waterproof without changing the Decision classifier and reports it missing");
        var wrongProduct = await provider.GetAsync(specRequest with { ProductReference =
            new ProductReference("2000000000001", "Fixture", DateTimeOffset.Now, specContext.CaptureId, "ObservedNotVerified") });
        check(!wrongProduct.Success && wrongProduct.ReturnedFields is null && wrongProduct.MissingFields.SequenceEqual(["Capacity"]),
            "provider never falls back to the test record for an unknown product ID");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV117", "product-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var extraJson = Path.Combine(root, "extra-field.json");
        File.WriteAllText(extraJson, File.ReadAllText(source).Replace("\"IsTestData\": true",
            "\"IsTestData\": true, \"ApiKey\": \"must-reject\""));
        try { _ = new JsonProductKnowledgeRepository(extraJson); throw new Exception("unknown top-level property accepted"); }
        catch (JsonException) { check(true, "local test record rejects unmapped credential-like properties"); }
        try { await new QnocrShadowRunner().RunAsync(new ShadowRunOptions(ProductKnowledgePath: source));
            throw new Exception("local test facts accepted without AI Shadow opt-in"); }
        catch (InvalidDataException) { check(true, "local product facts require explicit isolated AI Shadow mode"); }


        var invalidJson = Path.Combine(root, "invalid-product.json");
        File.WriteAllText(invalidJson, JsonSerializer.Serialize(new ProductKnowledgeRecord(id,
            new Dictionary<string, string> { ["Waterproof"] = "IPX8" }, "LocalTestProductConfig", DateTimeOffset.Now, true, "seller-test", "test-v1")));
        try { _ = new JsonProductKnowledgeRepository(invalidJson); throw new Exception("invalid product field accepted"); }
        catch (InvalidDataException) { check(true, "single-product source refuses unapproved fields instead of importing a catalog"); }

        async Task<ReplyPreview> Dispatch(string name, SessionContextView context, string fakeReply,
            Action<string> inspectContext)
        {
            var state = Path.Combine(root, name + "-state");
            var run = Path.Combine(root, name + "-run");
            var trace = new AgentPipelineTrace(run);
            var session = new MessageSession(sid, "seller-test", "buyer-test", "fixture", "Test",
                context.ProductId, "test-mapping.json");
            var item = new DispatchItem(context.MessageId, new string('D', 64), session,
                new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, context.ProductId,
                    [context.Messages[^1]]), DateTimeOffset.Now, true,
                CaptureId: context.CaptureId, AiRequestId: context.AiRequestId);
            var calls = 0;
            using var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
                (envelopePath, _, attemptDirectory, _) =>
                {
                    calls++;
                    var modelContext = Path.Combine(attemptDirectory, "model-context.json");
                    inspectContext(modelContext);
                    check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(envelopePath)!, "context.json"))
                        .Contains("\"Knowledge\"") == false &&
                        File.ReadAllText(envelopePath).Contains("\"Knowledge\"") == false,
                        "original Session Context and MessageEnvelope remain unchanged when test knowledge is attached");
                    return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                        fakeReply, 50, DateTimeOffset.Now, 1));
                }, true, trace, knowledgeProvider: provider, enrichAiContext: true);
            dispatch.Submit(item, context); dispatch.Start(); await dispatch.StopAsync();
            check(calls == 1 && dispatch.Snapshot().Single().State == "Completed" &&
                dispatch.Snapshot().Single().AiCalls == 1,
                "knowledge sidecar reaches unchanged AI callback once with Send OFF");
            var path = Path.Combine(state, "outbox", item.MessageId, "reply-preview.json");
            return JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(path))!;
        }
        var specPreview = await Dispatch("capacity", specContext, "这款有100ml和200ml可选。", modelPath =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
            var fact = doc.RootElement.GetProperty("Knowledge").GetProperty("FactFields").GetProperty("Capacity").GetString();
            check(fact?.Contains("200ml") == true && doc.RootElement.GetProperty("Knowledge").GetProperty("Status").GetString() == "Available",
                "AI model context receives bounded Capacity test fact with Available status");
        });
        check(specPreview.Reply.Contains("200ml") && specPreview.KnowledgeStatus == KnowledgeStatuses.Available &&
            specPreview.KnowledgeSource == "LocalTestProductConfig" && specPreview.KnowledgeUsedByAi &&
            !specPreview.KnowledgeGuardApplied && specPreview.SendStatus == "Shadow",
            "specification preview preserves fact-based test reply and explicit knowledge provenance");
        var materialPreview = await Dispatch("material", materialContext, "材质是TA1纯钛。", modelPath =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
            check(doc.RootElement.GetProperty("Knowledge").GetProperty("FactFields").GetProperty("Material").GetString() == "TA1纯钛",
                "material fact enters only the per-attempt model context");
        });
        check(materialPreview.KnowledgeStatus == KnowledgeStatuses.Available && materialPreview.Reply.Contains("TA1纯钛"),
            "material preview uses the single test record");
        var waterproofPreview = await Dispatch("waterproof", waterproofContext, "支持IPX8防水。", modelPath =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(modelPath));
            var knowledge = doc.RootElement.GetProperty("Knowledge");
            check(knowledge.GetProperty("Status").GetString() == "Missing" &&
                knowledge.GetProperty("FactFields").EnumerateObject().Count() == 0 &&
                knowledge.GetProperty("MissingFields")[0].GetString() == "Waterproof",
                "unknown Waterproof field enters model context only as Missing, with no fabricated facts");
        });
        check(waterproofPreview.KnowledgeStatus == KnowledgeStatuses.Missing && waterproofPreview.KnowledgeGuardApplied &&
            !waterproofPreview.KnowledgeUsedByAi && !waterproofPreview.Reply.Contains("IPX8") &&
            waterproofPreview.Reply.Contains("人工核实") && waterproofPreview.SendStatus == "Shadow",
            "unsupported waterproof claim is replaced by an explicit human-review fallback despite a fake hallucinated AI result");
        var traceText = File.ReadAllText(Path.Combine(root, "capacity-run", "agent-pipeline.jsonl"));
        check(traceText.Contains("KNOWLEDGE_CONTEXT_ATTACHED") && traceText.Contains("ReturnedFields") &&
            traceText.Contains("Capacity") && !traceText.Contains("100ml") && !traceText.Contains("200ml"),
            "knowledge trace logs returned field names without local test values");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "single-product PoC creates no send request or automatic delivery");
    }
}
