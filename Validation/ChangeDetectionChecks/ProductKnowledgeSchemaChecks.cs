using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ProductKnowledgeSchemaChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV120", "schema_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var source = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.schema.sample.json"));
        IProductKnowledgeRepository repository = new JsonProductKnowledgeRepository(source);
        var record = await repository.GetAsync("seller-test", "3000000000002")
            ?? throw new Exception("Schema fixture record missing.");
        var profile = record.Profile ?? throw new Exception("Schema fixture profile missing.");
        check(profile.Identity.SellerId == "seller-test" && profile.Identity.ProductId == "3000000000002" &&
            profile.Specification?.Capacity?.SequenceEqual(["100ml", "200ml"]) == true &&
            profile.Material?.MaterialName == "TA1纯钛" && profile.FAQ?.Count == 1 &&
            profile.Restrictions?.Count == 1 && profile.Metadata?.Version == "schema-test-v1",
            "one typed test profile contains required identity, specification, material, one FAQ and one restriction");
        check(profile.Features is null && profile.Scenarios is null && profile.AfterSales is null &&
            record.Fields.Count == 0,
            "optional knowledge sections and legacy Fields may be absent in a schema-only record");
        var optionalSections = profile with
        {
            Features = [new ProductFeature("测试卖点", "仅供Schema验证")],
            Scenarios = [new ProductScenario("Gift", "合成送礼场景")],
            AfterSales = new ProductAfterSales("合成售后条款", null)
        };
        ProductKnowledgeSchemaValidator.Validate(optionalSections, record);
        check(optionalSections.Features?.Count == 1 && optionalSections.Scenarios?.Count == 1 &&
            optionalSections.AfterSales?.ReturnPolicy == "合成售后条款",
            "optional feature, scenario and after-sales sections fit the same typed schema");
        var provider = new ProductKnowledgeProvider(repository);
        var classifier = new RuleBasedIntentClassifier();
        var sessionId = new string('A', 64);
        SessionContextView Context(string question, string messageId) => new(sessionId, "buyer-test", record.ProductId,
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "schema-fixture:frame_000001", messageId, new string('C', 64), 1);
        var specContext = Context("这款有200ml规格吗？", new string('1', 64));
        var specRequest = KnowledgeRequestBuilder.Build(specContext, classifier.Classify(specContext), "seller-test");
        var capacity = await provider.GetAsync(specRequest);
        check(capacity.Success && capacity.ReturnedFields?["Capacity"] == "100ml, 200ml" &&
            capacity.KnowledgeCategory == KnowledgeCategories.Specification && capacity.ProductVersion == "schema-test-v1",
            "provider maps typed specification to a versioned Specification KnowledgeResult");
        var mixed = await provider.GetAsync(specRequest with { NeedFields = ["Capacity", "Material"] });
        check(mixed.Success && mixed.KnowledgeCategory == KnowledgeCategories.Mixed &&
            mixed.ReturnedFields?.Count == 2,
            "multi-category result is labeled Mixed rather than misattributed to one source category");
        var materialContext = Context("什么材质？", new string('2', 64));
        var material = await provider.GetAsync(KnowledgeRequestBuilder.Build(materialContext,
            classifier.Classify(materialContext), "seller-test"));
        check(material.Success && material.ReturnedFields?["Material"] == "TA1纯钛" &&
            material.KnowledgeCategory == KnowledgeCategories.Material,
            "provider maps typed material to Material KnowledgeCategory without FAQ retrieval");
        var size = await provider.GetAsync(specRequest with { NeedFields = ["Size"] });
        check(size.Success && size.KnowledgeCategory == KnowledgeCategories.Specification &&
            size.ReturnedFields?["Size"] == "测试尺寸",
            "optional structured Size is addressable without changing the existing Knowledge interface");
        var minimal = record with { ProductId = "3000000000003", Version = "minimal-test-v1", Fields = new(),
            Profile = new ProductKnowledgeProfile(new ProductKnowledgeIdentity("seller-test", "3000000000003")) };
        var minimalPath = Path.Combine(root, "minimal.json");
        File.WriteAllText(minimalPath, JsonSerializer.Serialize(minimal));
        var minimalRepository = new JsonProductKnowledgeRepository(minimalPath);
        var minimalRequest = specRequest with { ProductReference = new ProductReference(minimal.ProductId,
            "Fixture", DateTimeOffset.Now, specContext.CaptureId, "ObservedNotVerified") };
        var minimalResult = await new ProductKnowledgeProvider(minimalRepository).GetAsync(minimalRequest);
        check(!minimalResult.Success && !minimalResult.MissingProduct &&
            minimalResult.MissingFields.SequenceEqual(["Capacity"]),
            "identity-only profile is valid while absent optional specification returns a missing field");
        void Reject(ProductKnowledgeRecord invalid, string message)
        {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(invalid));
            try { _ = new JsonProductKnowledgeRepository(path); throw new Exception("invalid schema accepted: " + message); }
            catch (InvalidDataException) { check(true, message); }
        }
        Reject(record with { Profile = profile! with { Identity = new ProductKnowledgeIdentity("seller-test", "") } },
            "profile ProductId is required");
        Reject(record with { Profile = profile! with { Identity = new ProductKnowledgeIdentity("", record.ProductId) } },
            "profile SellerId is required");
        Reject(record with { ProductId = "" }, "outer ProductId is required");
        Reject(record with { SellerId = "" }, "outer SellerId is required");
        Reject(record with { Profile = profile! with { Identity = new ProductKnowledgeIdentity("seller-other", record.ProductId) } },
            "profile identity must match outer seller scope");
        Reject(record with { Profile = profile! with { FAQ = [new ProductFAQ("是否支持刻字？", "")] } },
            "FAQ question and answer must both be nonempty when FAQ is present");
        Reject(record with { Profile = profile! with { Restrictions = [new ProductRestriction("")] } },
            "restriction text must be nonempty when a restriction is present");
        Reject(record with { Profile = profile! with { Metadata = profile.Metadata! with { Version = "wrong" } } },
            "profile metadata must match outer version/source/time");
        Reject(record with { Profile = profile with
            { Specification = new ProductSpecification(Enumerable.Repeat("1234567890ml", 16).ToArray()) } },
            "oversized combined capacity values are rejected before AI context composition");
        Reject(record with { Fields = new() { ["Capacity"] = "500ml" } },
            "conflicting legacy Fields and typed specification are rejected");
        if (record.Profile?.Specification?.Capacity is List<string> mutableCapacity) mutableCapacity[0] = "tampered";
        var fresh = await repository.GetAsync("seller-test", "3000000000002");
        check(fresh?.Profile?.Specification?.Capacity?[0] == "100ml",
            "repository returns a defensive deep copy of structured knowledge sections");

        var state = Path.Combine(root, "state"); var run = Path.Combine(root, "run");
        var session = new MessageSession(sessionId, "seller-test", "buyer-test", "fixture", "Test",
            record.ProductId, "test-mapping.json");
        var item = new DispatchItem(specContext.MessageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, record.ProductId,
                [specContext.Messages[^1]]), DateTimeOffset.Now, true,
            CaptureId: specContext.CaptureId, AiRequestId: specContext.AiRequestId);
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
            (_, _, attempt, _) =>
            {
                aiCalls++;
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                var knowledge = model.RootElement.GetProperty("Knowledge");
                check(knowledge.GetProperty("KnowledgeCategory").GetString() == "Specification" &&
                    knowledge.GetProperty("FactFields").GetProperty("Capacity").GetString() == "100ml, 200ml" &&
                    !model.RootElement.TryGetProperty("FAQ", out _) &&
                    !model.RootElement.TryGetProperty("Restrictions", out _),
                    "AI model context gets only requested specification, never whole FAQ or restriction sections");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试样例包含100ml与200ml。", 35, DateTimeOffset.Now, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(item, specContext); dispatch.Start(); await dispatch.StopAsync();
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state, "outbox",
                item.MessageId, "reply-preview.json")))!;
            check(aiCalls == 1 && preview.KnowledgeStatus == "Available" &&
                preview.KnowledgeCategory == KnowledgeCategories.Specification &&
                preview.KnowledgeUsedByAi && preview.Reply.Contains("200ml") && preview.SendStatus == "Shadow",
                "typed schema still reaches unchanged synthetic AI callback and a categorized Shadow preview");
        }
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "schema PoC creates no send request");
    }
}
