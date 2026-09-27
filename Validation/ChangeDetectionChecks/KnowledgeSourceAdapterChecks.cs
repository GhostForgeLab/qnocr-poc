using System.Text.Json;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class KnowledgeSourceAdapterChecks
{
    private sealed class MemorySource : IKnowledgeSource
    {
        public KnowledgeStorageSourceType SourceType => KnowledgeStorageSourceType.Database;
        public Task<ProductKnowledgeRecord?> ReadAsync(string sellerId, string productId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProductKnowledgeRecord? record = sellerId == "seller-memory" && productId == "9000000000001"
                ? new ProductKnowledgeRecord(productId, new Dictionary<string, string> { ["Capacity"] = "350ml" },
                    "LocalTestProductConfig", DateTimeOffset.UtcNow, true, sellerId, "memory-v1")
                : null;
            return Task.FromResult(record);
        }
        public Task<KnowledgeItem?> ReadPublishedVersionAsync(string sellerId, string productId,
            string key, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<KnowledgeItem?>(null);
    }
    private sealed class WrongScopeSource : IKnowledgeSource
    {
        public KnowledgeStorageSourceType SourceType => KnowledgeStorageSourceType.Feishu;
        public Task<ProductKnowledgeRecord?> ReadAsync(string sellerId, string productId,
            CancellationToken cancellationToken = default) => Task.FromResult<ProductKnowledgeRecord?>(
                new ProductKnowledgeRecord(productId, new Dictionary<string, string> { ["Capacity"] = "999ml" },
                    "LocalTestProductConfig", DateTimeOffset.UtcNow, true, "other-seller", "wrong-v1"));
        public Task<KnowledgeItem?> ReadPublishedVersionAsync(string sellerId, string productId,
            string key, string version, CancellationToken cancellationToken = default) =>
            Task.FromResult<KnowledgeItem?>(null);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV125", "source_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var filePath = Path.GetFullPath(Path.Combine("Knowledge", "product-knowledge.json"));
        IKnowledgeSource local = new LocalFileKnowledgeSource(filePath);
        var raw = await local.ReadAsync("seller-test", "1051087533910");
        check(local.SourceType == KnowledgeStorageSourceType.LocalFile &&
            raw?.Fields["Capacity"] == "100ml, 200ml" && raw.Version == "test-v1",
            "LocalFileKnowledgeSource reads the unchanged legacy product JSON");
        var repository = new ProductKnowledgeRepository(local);
        var fetched = await repository.GetAsync("seller-test", "1051087533910");
        check(fetched?.StorageSourceType == KnowledgeStorageSourceType.LocalFile &&
            fetched.Fields["Material"] == "TA1纯钛" &&
            await repository.GetAsync("seller-other", "1051087533910") is null,
            "generic repository delegates to local source and preserves seller scoping");
        var legacy = await new JsonProductKnowledgeRepository(filePath).GetAsync("seller-test", "1051087533910");
        check(legacy?.Fields.OrderBy(pair => pair.Key).SequenceEqual(fetched!.Fields.OrderBy(pair => pair.Key)) == true &&
            legacy.StorageSourceType == KnowledgeStorageSourceType.LocalFile,
            "JsonProductKnowledgeRepository compatibility constructor preserves results");
        var memoryRepository = new ProductKnowledgeRepository(new MemorySource());
        var memoryRecord = await memoryRepository.GetAsync("seller-memory", "9000000000001");
        check(memoryRecord?.StorageSourceType == KnowledgeStorageSourceType.Database &&
            memoryRecord.Fields["Capacity"] == "350ml",
            "repository accepts an in-memory stand-in for a future Database source without file paths");
        try { await new ProductKnowledgeRepository(new WrongScopeSource()).GetAsync("seller-test", "9000000000001");
            throw new Exception("source scope mismatch accepted"); }
        catch (InvalidDataException) { check(true, "repository rejects out-of-scope records from any source adapter"); }
        var provider = new ProductKnowledgeProvider(repository);
        var request = new KnowledgeRequest(new string('A', 64),
            new ProductReference("1051087533910", "Fixture", DateTimeOffset.Now, "source-fixture:capture",
                "ObservedNotVerified"), "PRODUCT_SPEC_QUERY", "容量是多少？", ["Capacity"],
            new string('1', 64), "source-fixture:capture", DateTimeOffset.Now, "seller-test");
        var result = await provider.GetAsync(request);
        check(result.Success && result.ReturnedFields?["Capacity"] == "100ml, 200ml" &&
            result.Source == "LocalTestProductConfig" && result.ProductVersion == "test-v1" &&
            result.StorageSourceType == KnowledgeStorageSourceType.LocalFile,
            "Provider output stays unchanged while source-type metadata is attached");
        var sourceMiss = await provider.GetAsync(request with { ProductReference = new ProductReference(
            "9999999999999", "Fixture", DateTimeOffset.Now, request.CaptureId, "ObservedNotVerified") });
        check(!sourceMiss.Success && sourceMiss.MissingProduct &&
            sourceMiss.StorageSourceType == KnowledgeStorageSourceType.LocalFile,
            "a valid source lookup retains LocalFile metadata even when the product is absent");
        var memoryResult = await new ProductKnowledgeProvider(memoryRepository).GetAsync(request with
        {
            SellerId = "seller-memory", ProductReference = new ProductReference("9000000000001",
                "Fixture", DateTimeOffset.Now, "source-fixture:capture", "ObservedNotVerified")
        });
        check(memoryResult.Success && memoryResult.ReturnedFields?["Capacity"] == "350ml" &&
            memoryResult.StorageSourceType == KnowledgeStorageSourceType.Database,
            "Provider can consume another source through the same repository contract");
        var context = new SessionContextView(request.SessionId, "buyer-test", "1051087533910",
            [new EnvelopeMessage("buyer", "容量是多少？", new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, request.CaptureId, request.MessageId, new string('C', 64), 1);
        var session = new MessageSession(request.SessionId, "seller-test", "buyer-test", "fixture", "Test",
            "1051087533910", "test-mapping.json");
        var dispatchItem = new DispatchItem(request.MessageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, session.ProductId,
                [context.Messages[^1]]), DateTimeOffset.Now, true, CaptureId: context.CaptureId,
            AiRequestId: context.AiRequestId);
        var trace = new AgentPipelineTrace(root);
        check(trace.KnowledgeResolved(dispatchItem, context, request, result),
            "knowledge read and result events are both persisted");
        var rows = File.ReadAllLines(Path.Combine(root, "agent-pipeline.jsonl"))
            .Select(row => JsonDocument.Parse(row).RootElement.Clone()).ToArray();
        var sourceRow = rows.Single(row => row.GetProperty("Event").GetString() == "KNOWLEDGE_SOURCE_READ");
        check(sourceRow.GetProperty("StorageSourceType").GetString() == "LocalFile" &&
            sourceRow.GetProperty("SellerId").GetString() == "seller-test" &&
            sourceRow.GetProperty("ProductReference").GetString() == "1051087533910" &&
            sourceRow.GetProperty("ProductVersion").GetString() == "test-v1" &&
            !File.ReadAllText(Path.Combine(root, "agent-pipeline.jsonl")).Contains("100ml"),
            "KNOWLEDGE_SOURCE_READ logs source type, seller, product and version without the fact value");
        var state = Path.Combine(root, "dispatch"); var run = Path.Combine(root, "run");
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"), CancellationToken.None,
            (_, _, attempt, _) =>
            {
                aiCalls++;
                using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "model-context.json")));
                check(model.RootElement.GetProperty("Knowledge").GetProperty("FactFields")
                    .GetProperty("Capacity").GetString() == "100ml, 200ml",
                    "unchanged model context contains only requested local test facts");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "测试容量有100ml和200ml。", 20, DateTimeOffset.Now, 1));
            }, true, new AgentPipelineTrace(run), knowledgeProvider: provider, enrichAiContext: true))
        {
            dispatch.Submit(dispatchItem, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", dispatchItem.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.KnowledgeStatus == KnowledgeStatuses.Available &&
            preview.KnowledgeUsedByAi && preview.SendStatus == "Shadow",
            "source adapter preserves one synthetic AI call and Shadow reply preview");
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "source adapter creates no send request");
    }
}
