using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Product;

internal static class AgentContextKnowledgeChecks
{
    private const string SellerA = "seller-A", SellerB = "seller-B";
    private const string ProductA = "1051087533910", ProductB = "1111111111111", ProductC = "2222222222222";
    private static readonly DateTime MessageTime = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Unspecified);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _ticks += elapsed.Ticks; }
    }
    private sealed class JevHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Choice { get; set; } = "MATERIAL_QUERY";
        public bool Fail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var names = new[] { "PRODUCT_SPEC_QUERY", "MATERIAL_QUERY", "SHIPPING_QUERY",
                "CUSTOMIZATION_QUERY", "PRICE_QUERY", "AFTER_SALES_QUERY", "GIFT_QUERY", "COLLECTION_QUERY", "OTHER" };
            var body = JsonSerializer.Serialize(new
            {
                model = "jev-v1350b-controlled", answers = new { intent = new
                {
                    type = "choice", choice = Choice, confidence = 1.0,
                    probabilities = names.ToDictionary(name => name, name => name == Choice ? 1.0 : 0.0)
                } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string ProductState { get; }
        public string ProductFile { get; }
        public string Database { get; }
        public KnowledgeReviewService Workflow { get; }
        public Clock Clock { get; } = new();
        public JevHandler Handler { get; } = new();
        public AgentKnowledgePipeline Pipeline { get; private set; }
        public List<AgentKnowledgeMessageResult> Results { get; } = [];
        public Dictionary<string, KnowledgeItem> Published { get; } = [];
        private HttpClient _client;
        public Fixture(string parent, string name)
        {
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
            ProductState = Path.Combine(Root, "product-sidecars");
            ProductFile = Path.Combine(Root, "products.json");
            Database = Path.Combine(Root, "sessions.db");
            var records = new[]
            {
                Record(SellerA, ProductA, "LEGACY-A", "999ml"), Record(SellerA, ProductB, "LEGACY-B", "888ml"),
                Record(SellerB, ProductA, "LEGACY-OTHER-SELLER", "777ml"),
                Record(SellerA, ProductC, "LEGACY-UNPUBLISHED", "666ml")
            };
            File.WriteAllText(ProductFile, JsonSerializer.Serialize(new { Products = records }));
            Workflow = new KnowledgeReviewService(Path.Combine(Root, "workflow"));
            Published["A-Material"] = Publish(SellerA, ProductA, "Material", "A-TA1纯钛", KnowledgeCategories.Material);
            Published["A-Capacity"] = Publish(SellerA, ProductA, "Capacity", "A-200ml", KnowledgeCategories.Specification);
            Published["A-Shipping"] = Publish(SellerA, ProductA, "ShippingTime", "A-48小时内发货", KnowledgeCategories.FAQ);
            Published["A-Return"] = Publish(SellerA, ProductA, "ReturnPolicy", "A-测试售后政策", KnowledgeCategories.AfterSales);
            Published["B-Material"] = Publish(SellerA, ProductB, "Material", "B-304不锈钢", KnowledgeCategories.Material);
            Published["OtherSeller-Material"] = Publish(SellerB, ProductA, "Material", "OTHER-玻璃", KnowledgeCategories.Material);
            _client = new HttpClient(Handler, disposeHandler: false);
            Pipeline = CreatePipeline();
        }
        private ProductKnowledgeRecord Record(string seller, string product, string material, string capacity) =>
            new(product, new Dictionary<string, string> { ["Material"] = material, ["Capacity"] = capacity },
                "LocalTestProductConfig", Clock.GetUtcNow(), true, seller, "legacy-test");
        public KnowledgeItem Draft(string seller, string product, string key, string value, string category = KnowledgeCategories.Specification) =>
            Workflow.CreateDraft(seller, product, category, key, value,
                new KnowledgeSource(KnowledgeSourceType.ManualInput, "controlled-v1350b",
                    "fixture:v1350b/" + product + "/" + key, Clock.GetUtcNow()), "controlled-v1", "fixture-author");
        public KnowledgeItem Publish(string seller, string product, string key, string value, string category)
        {
            var item = Draft(seller, product, key, value, category);
            Workflow.SubmitForReview(item.KnowledgeId, "fixture-author");
            Workflow.Approve(item.KnowledgeId, "fixture-reviewer");
            return Workflow.Publish(item.KnowledgeId, "fixture-publisher", allowAiUse: true);
        }
        private AgentKnowledgePipeline CreatePipeline() => new(Database, ProductState, ProductFile, Workflow.ItemsPath,
            timeProvider: Clock, jevClient: _client, apiKey: () => "controlled-key");
        public void Restart()
        {
            Pipeline.Dispose(); _client.Dispose();
            _client = new HttpClient(Handler, disposeHandler: false); Pipeline = CreatePipeline();
        }
        public async Task<AgentKnowledgeBatchResult> Process(MessageEnvelope envelope, OcrResult? ocr = null, string capture = "controlled-capture")
        {
            var result = await Pipeline.ProcessMessageAsync(envelope, ocr, capture, Clock.GetUtcNow());
            Results.AddRange(result.Results); return result;
        }
        public string Bind(AgentSession session, string product)
        {
            var alias = Pipeline.PrepareManualContext(session.SessionId);
            new ManualProductResolver(ProductState).Bind(alias, session.Identity.SellerId, product,
                "明确选择的测试商品", "fixture-operator", Clock.GetUtcNow());
            return alias;
        }
        public void Dispose() { Pipeline.Dispose(); _client.Dispose(); Handler.Dispose(); }
    }
    private static MessageEnvelope Envelope(string buyer, string question, int sequence, string seller = SellerA, string product = "") =>
        new("Qianniu.OCR", seller, buyer, product, [new EnvelopeMessage("buyer", question, MessageTime.AddSeconds(sequence))]);
    private static OcrResult Ocr(string product, float confidence = .95f, string? other = null) =>
        new("controlled-product-region", "fixture-only", new[]
        {
            new OcrLine(1, "商品ID: " + product, confidence, new OcrBounds(0, 0, 250, 20)),
            new OcrLine(2, "商品名称: 明确标记的测试商品", .95f, new OcrBounds(0, 30, 350, 20))
        }.Concat(other is null ? [] : new[] { new OcrLine(3, "商品ID: " + other, .95f, new OcrBounds(0, 60, 250, 20)) }).ToArray(),
            0, confidence, 0, 0, 0, 0, 0, 0);
    private static void Reject<T>(Action action, Action<bool, string> check, string description) where T : Exception
    {
        try { action(); } catch (T) { check(true, description); return; }
        check(false, description);
    }
    private static void Available(AgentKnowledgeMessageResult result, string field, string value, Action<bool, string> check, string label) =>
        check(result.QueryStatus == KnowledgeStatuses.Available && result.ProviderCalled &&
            result.Provider == nameof(ProductKnowledgeProvider) && result.Knowledge?.ReturnedFields?[field] == value &&
            result.HitCount == 1 && result.Error is null, label);

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1350B", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var fixtures = new List<Fixture>();
        Fixture New(string name) { var f = new Fixture(root, name); fixtures.Add(f); return f; }
        using (var f = New("manual-isolation"))
        {
            var workflowBefore = File.ReadAllText(f.Workflow.ItemsPath);
            var productBefore = File.ReadAllText(f.ProductFile);
            var initial = await f.Process(Envelope("buyer-A", "这个酒壶是什么材质", 1));
            var held = initial.Results.Single();
            check(held.QueryStatus == KnowledgeStatuses.MissingProductContext && !held.ProviderCalled &&
                held.Request?.ProductReference is null && f.Pipeline.KnowledgeCalls == 0,
                "Context→Intent with no Product Context skips the Knowledge Provider completely");
            check(initial.IntentResults.Single().Provider == "JEV" && initial.Session.Messages.Count == 1,
                "new Knowledge integration consumes the real V1.35.0-A factory/JEV result after Context save");
            var alias = f.Bind(initial.Session, ProductA);
            check(alias.Length == 64 && initial.Session.SessionId.Length == 32 &&
                alias == initial.ResolverSessionId && alias != initial.Session.SessionId,
                "namespaced product projection adapts Agent 32-hex ID to unchanged Manual Resolver 64-hex contract");
            var result = (await f.Process(Envelope("buyer-A", "继续问材质", 2))).Results.Single();
            Available(result, "Material", "A-TA1纯钛", check, "existing Manual Resolver and Published Knowledge source hit Product A material");
            check(result.SessionId == initial.Session.SessionId && result.MessageId == result.Intent.MessageId &&
                result.SellerId == SellerA && result.ProductId == ProductA &&
                result.Intent.Intent?.IntentName == "MATERIAL_QUERY" && result.Timestamp != default,
                "Knowledge result links original Agent Session, stable message, seller, product and Intent");
            check(result.Request?.SessionId == initial.Session.SessionId && result.Request.Intent == "MATERIAL_QUERY" &&
                result.Request.Query == "继续问材质" && result.Request.NeedFields.SequenceEqual(["Material"]) &&
                result.Request.ProductReference?.AssociationStatus == "BoundCurrentSession" &&
                result.ProductContext.Current?.SessionId == initial.Session.SessionId,
                "existing KnowledgeRequestBuilder receives the mapped-back bound context and current question");
            check(result.Knowledge?.KnowledgeId == f.Published["A-Material"].KnowledgeId &&
                result.Knowledge.ProductVersion == "controlled-v1" &&
                result.Knowledge.ItemSource?.Reference == "fixture:v1350b/" + ProductA + "/Material" &&
                result.Knowledge.StorageSourceType == KnowledgeStorageSourceType.LocalFile,
                "existing KnowledgeResult retains Published item ID, version, source reference and storage type");
            var calls = f.Pipeline.KnowledgeCalls;
            var replay = await f.Process(Envelope("buyer-A", "继续问材质", 2, product: ProductB), Ocr(ProductB), "different-capture");
            check(replay.Results.Count == 0 && replay.IntentResults.Count == 0 && f.Pipeline.KnowledgeCalls == calls,
                "same stable message replay cannot repeat Knowledge or change its product by replacing Envelope/sidecar data");
            f.Handler.Choice = "PRODUCT_SPEC_QUERY";
            Available((await f.Process(Envelope("buyer-A", "那200ml的呢", 3))).Results.Single(),
                "Capacity", "A-200ml", check, "specification Intent reuses existing Capacity field selection");
            f.Handler.Choice = "SHIPPING_QUERY";
            var shipping = (await f.Process(Envelope("buyer-A", "什么时候发货", 4))).Results.Single();
            Available(shipping, "ShippingTime", "A-48小时内发货", check, "shipping Intent queries only the current seller/product Published ShippingTime");
            check(!shipping.Intent.Intent!.NeedProductContext && shipping.Request?.ProductReference?.ProductId == ProductA,
                "shipping still requires current product evidence even when Intent NeedProductContext is false");
            f.Handler.Choice = "AFTER_SALES_QUERY";
            Available((await f.Process(Envelope("buyer-A", "售后怎么处理", 5))).Results.Single(),
                "ReturnPolicy", "A-测试售后政策", check, "after-sales Intent preserves existing ReturnPolicy mapping");
            f.Handler.Choice = "GIFT_QUERY";
            var gift = (await f.Process(Envelope("buyer-A", "适合送礼吗", 6))).Results.Single();
            check(gift.Request?.Intent == "GIFT_QUERY" && gift.Request.NeedFields.SequenceEqual(["Unspecified"]) &&
                gift.QueryStatus == KnowledgeStatuses.Missing && gift.HitCount == 0,
                "existing gift/scenario Intent is associated correctly without inventing a new query algorithm");
            f.Handler.Choice = "OTHER"; calls = f.Pipeline.KnowledgeCalls;
            check((await f.Process(Envelope("buyer-A", "你好", 7))).Results.Single().QueryStatus == KnowledgeStatuses.NotRequired &&
                f.Pipeline.KnowledgeCalls == calls, "NeedKnowledge=false skips the provider using the existing NotRequired status");
            f.Handler.Choice = "MATERIAL_QUERY";
            var bInitial = await f.Process(Envelope("buyer-B", "B问材质", 8));
            check(bInitial.Results.Single().QueryStatus == KnowledgeStatuses.MissingProductContext &&
                f.Pipeline.KnowledgeCalls == calls, "new Session B never borrows Session A's manual product");
            var aliasB = f.Bind(bInitial.Session, ProductB);
            var bResult = (await f.Process(Envelope("buyer-B", "B继续材质", 9))).Results.Single();
            Available(bResult, "Material", "B-304不锈钢", check, "same seller, different product returns only Product B facts");
            check(alias != aliasB && bResult.SessionId != result.SessionId && bResult.ProductId != result.ProductId,
                "product resolver sidecars and Knowledge associations are separated by original Agent Session");
            var otherInitial = await f.Process(Envelope("buyer-A", "另店问材质", 10, SellerB));
            f.Bind(otherInitial.Session, ProductA);
            Available((await f.Process(Envelope("buyer-A", "另店继续问", 11, SellerB))).Results.Single(),
                "Material", "OTHER-玻璃", check, "same ProductId across different sellers returns the exact seller's Published facts");
            var unknownInitial = await f.Process(Envelope("buyer-unknown", "材质", 12, "seller-unknown"));
            f.Bind(unknownInitial.Session, ProductA);
            var unknown = (await f.Process(Envelope("buyer-unknown", "继续材质", 13, "seller-unknown"))).Results.Single();
            check(unknown.QueryStatus == KnowledgeStatuses.Missing && unknown.Knowledge?.MissingProduct == true &&
                unknown.HitCount == 0, "unmapped seller cannot fall back to another seller's Product A");
            calls = f.Pipeline.KnowledgeCalls;
            try { await f.Pipeline.ProcessMessageAsync(Envelope("buyer-missing", "材质", 14, "")); check(false, "missing seller rejected"); }
            catch (ArgumentException) { check(f.Pipeline.KnowledgeCalls == calls, "missing SellerId is rejected by Context before any Knowledge query"); }
            var priorSession = f.Pipeline.GetSession(initial.Session.SessionId)!;
            f.Restart();
            check(f.Pipeline.KnowledgeCalls == 0 && f.Pipeline.GetSession(initial.Session.SessionId)!.Messages.SequenceEqual(priorSession.Messages),
                "SQLite component reconstruction restores Context without querying historical Knowledge");
            check((await f.Process(Envelope("buyer-A", "继续问材质", 2))).Results.Count == 0 &&
                f.Pipeline.KnowledgeCalls == 0, "restored message replay runs neither Intent nor Knowledge");
            Available((await f.Process(Envelope("buyer-A", "恢复后新材质", 15))).Results.Single(),
                "Material", "A-TA1纯钛", check, "new message after restore can reuse its own unexpired explicit manual binding");
            check(File.ReadAllText(f.Workflow.ItemsPath) == workflowBefore && File.ReadAllText(f.ProductFile) == productBefore,
                "Agent runtime reads Knowledge without modifying items, workflow, publication or legacy product content");
            check(f.Pipeline.GetSession(initial.Session.SessionId)!.Identity.IdentityLevel == IdentityLevel.Observed &&
                f.Pipeline.GetSession(initial.Session.SessionId)!.CurrentProductContext is null &&
                f.Pipeline.GetSession(initial.Session.SessionId)!.IntentHistory.Count == 0,
                "successful Knowledge leaves original Agent identity Observed and reserved SQLite/business fields unchanged");
        }
        using (var f = New("ocr-and-held-context"))
        {
            var first = await f.Process(Envelope("ocr-buyer", "是什么材质", 1), Ocr(ProductA), "capture-A");
            var result = first.Results.Single();
            Available(result, "Material", "A-TA1纯钛", check, "existing labeled Product OCR Resolver fixture drives the Published Knowledge Provider");
            check(result.ProductContext.Current?.Source == ProductContextSource.OCR &&
                result.Request?.ProductReference?.CaptureId == "capture-A" && result.Request.BindingStatus == ProductContextBindingStatus.Bound &&
                result.ProductContext.Current?.CapturedAt == f.Clock.GetUtcNow(),
                "OCR ProductReference and BoundProductContext remain tied to the current capture");
            var calls = f.Pipeline.KnowledgeCalls;
            var absent = (await f.Process(Envelope("ocr-buyer", "继续问材质", 2, product: ProductB), capture: "capture-no-product")).Results.Single();
            check(absent.QueryStatus == KnowledgeStatuses.MissingProductContext && !absent.ProviderCalled &&
                f.Pipeline.KnowledgeCalls == calls, "no current product OCR cannot borrow prior OCR binding or Envelope ProductId");
            var low = (await f.Process(Envelope("ocr-buyer", "低可信材质", 3), Ocr(ProductA, .50f))).Results.Single();
            check(low.ProductContext.Status == ProductContextBindingStatus.Uncertain && !low.ProviderCalled &&
                f.Pipeline.KnowledgeCalls == calls, "low-confidence current product ID holds Knowledge");
            var ambiguous = (await f.Process(Envelope("ocr-buyer", "多商品材质", 4), Ocr(ProductA, other: ProductB))).Results.Single();
            check(ambiguous.ProductContext.Status == ProductContextBindingStatus.Uncertain && !ambiguous.ProviderCalled &&
                f.Pipeline.KnowledgeCalls == calls, "ambiguous current product OCR holds Knowledge without guessing a product");
            var missingCapture = (await f.Process(Envelope("ocr-buyer", "无帧标记材质", 5), Ocr(ProductA), "")).Results.Single();
            check(!missingCapture.ProviderCalled && f.Pipeline.KnowledgeCalls == calls,
                "OCR without current CaptureId cannot query Knowledge");
            f.Bind(first.Session, ProductA);
            var conflict = (await f.Process(Envelope("ocr-buyer", "冲突材质", 6), Ocr(ProductB))).Results.Single();
            check(conflict.ProductContext.Status == ProductContextBindingStatus.Conflict &&
                conflict.QueryStatus == KnowledgeStatuses.MissingProductContext && !conflict.ProviderCalled &&
                f.Pipeline.KnowledgeCalls == calls, "existing manual/OCR conflict policy prevents a wrong-product query");
            Available((await f.Process(Envelope("ocr-buyer", "一致材质", 7), Ocr(ProductA))).Results.Single(),
                "Material", "A-TA1纯钛", check, "agreeing manual and current OCR evidence preserves the existing manual selection");
            var bindingPath = Path.Combine(f.ProductState, "sessions", first.ResolverSessionId, "manual-product-binding.json");
            var original = File.ReadAllText(bindingPath);
            var binding = JsonSerializer.Deserialize<BoundProductContext>(original)!;
            foreach (var bad in new[] { binding with { SellerId = SellerB }, binding with { SessionId = new string('E', 64) },
                binding with { ProductId = "not-a-product" }, binding with { CapturedAt = f.Clock.GetUtcNow().AddHours(-3) } })
            {
                File.WriteAllText(bindingPath, JsonSerializer.Serialize(bad));
                calls = f.Pipeline.KnowledgeCalls;
                var held = (await f.Process(Envelope("ocr-buyer", "错误绑定材质", 20 + f.Results.Count))).Results.Single();
                check(held.QueryStatus == KnowledgeStatuses.MissingProductContext && !held.ProviderCalled &&
                    f.Pipeline.KnowledgeCalls == calls, "foreign seller/session, invalid product or expired manual binding is ignored safely");
            }
            File.WriteAllText(bindingPath, original);
            f.Clock.Advance(TimeSpan.FromHours(26));
            var fresh = await f.Process(Envelope("ocr-buyer", "新生命周期材质", 60));
            check(fresh.Session.SessionId != first.Session.SessionId && fresh.ResolverSessionId != first.ResolverSessionId &&
                fresh.Results.Single().QueryStatus == KnowledgeStatuses.MissingProductContext &&
                !fresh.Results.Single().ProviderCalled &&
                f.Pipeline.GetSession(first.Session.SessionId)?.Status == AgentSessionStatus.Archived,
                "new Agent lifecycle Session cannot inherit archived manual/OCR product context");
            Reject<InvalidDataException>(() => new AgentProductContextAdapter(f.ProductState).Prepare(
                first.Session with { Status = AgentSessionStatus.Archived }), check, "adapter rejects direct Archived Session projection");
            Reject<InvalidDataException>(() => new AgentProductContextAdapter(f.ProductState).Prepare(
                first.Session with { Identity = new SessionIdentity("Qianniu.OCR", "", "buyer") }), check,
                "adapter refuses a sellerless product projection");
        }
        using (var f = New("published-workflow"))
        {
            f.Handler.Choice = "PRODUCT_SPEC_QUERY";
            var draft = f.Draft(SellerA, ProductC, "Capacity", "DRAFT-123ml");
            var initial = await f.Process(Envelope("workflow-buyer", "200ml规格", 1));
            f.Bind(initial.Session, ProductC);
            async Task<AgentKnowledgeMessageResult> Read(int sequence) =>
                (await f.Process(Envelope("workflow-buyer", "那200ml的呢", sequence))).Results.Single();
            var draftResult = await Read(2);
            check(draftResult.QueryStatus == KnowledgeStatuses.Missing && draftResult.ProviderCalled &&
                draftResult.HitCount == 0 && !draftResult.Knowledge!.Content.Contains("666ml") &&
                !draftResult.Knowledge.Content.Contains("DRAFT"),
                "Draft is unavailable and legacy product facts never bypass workflow mode");
            f.Workflow.SubmitForReview(draft.KnowledgeId, "fixture-author");
            check((await Read(3)).HitCount == 0, "Reviewing knowledge does not enter normal Agent results");
            f.Workflow.Approve(draft.KnowledgeId, "fixture-reviewer");
            check((await Read(4)).HitCount == 0, "Approved but unpublished knowledge remains unavailable");
            f.Workflow.Publish(draft.KnowledgeId, "fixture-publisher", allowAiUse: false);
            check((await Read(5)).HitCount == 0, "Published without AiUseAllowed remains unavailable under existing workflow rules");
            var published = f.Publish(SellerA, ProductC, "Capacity", "PUBLISHED-200ml", KnowledgeCategories.Specification);
            var available = await Read(6);
            Available(available, "Capacity", "PUBLISHED-200ml", check, "Published approved Knowledge appears without an automatic runtime publication");
            check(available.Knowledge?.KnowledgeId == published.KnowledgeId,
                "published filtering retains the exact approved item reference");
        }
        using (var f = New("knowledge-failure-and-fallback"))
        {
            var first = await f.Process(Envelope("failure-buyer", "材质", 1), Ocr(ProductA));
            var workflow = File.ReadAllText(f.Workflow.ItemsPath);
            File.WriteAllText(f.Workflow.ItemsPath, "{");
            var calls = f.Pipeline.KnowledgeCalls;
            var failedEnvelope = Envelope("failure-buyer", "后续材质", 2);
            var failed = await f.Process(failedEnvelope, Ocr(ProductA));
            check(failed.Results.Single() is { QueryStatus: AgentKnowledgePipeline.KnowledgeUnavailable,
                ProviderCalled: true, Error: "JsonException", Knowledge: null, HitCount: 0 } &&
                f.Pipeline.KnowledgeCalls == calls + 1, "existing Knowledge Provider/source exception returns KnowledgeUnavailable without legacy fallback");
            check(failed.Session.Messages.Count == first.Session.Messages.Count + 1 &&
                new SQLiteAgentSessionStore(f.Database).LoadSessions().Single().Messages.Count == failed.Session.Messages.Count,
                "Knowledge exception does not crash or roll back the saved Agent Context");
            check((await f.Process(failedEnvelope, Ocr(ProductA))).Results.Count == 0 &&
                f.Pipeline.KnowledgeCalls == calls + 1, "failed Knowledge execution is not repeated for the same Intent/message");
            File.WriteAllText(f.Workflow.ItemsPath, workflow);
            Available((await f.Process(Envelope("failure-buyer", "修复后材质", 3), Ocr(ProductA))).Results.Single(),
                "Material", "A-TA1纯钛", check, "new message can query after the source is repaired");
            f.Handler.Fail = true;
            var fallback = (await f.Process(Envelope("failure-buyer", "材质是什么", 4), Ocr(ProductA))).Results.Single();
            Available(fallback, "Material", "A-TA1纯钛", check, "existing RuleBased Intent fallback can continue to the same Published Knowledge path");
            check(fallback.Intent.Provider == "RuleBased" && fallback.Intent.FallbackUsed &&
                fallback.Intent.Intent?.IntentName == "MATERIAL_QUERY",
                "Knowledge result preserves V1.35.0-A fallback provider association");
        }
        using (var f = New("concurrency-and-schema"))
        {
            var beforeWorkflow = File.ReadAllText(f.Workflow.ItemsPath);
            var envelope = Envelope("concurrent-buyer", "材质", 1);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                f.Pipeline.ProcessMessageAsync(envelope, Ocr(ProductA), "same-capture", f.Clock.GetUtcNow())));
            f.Results.AddRange(results.SelectMany(r => r.Results));
            check(f.Handler.Calls == 1 && f.Pipeline.KnowledgeCalls == 1 && results.Sum(r => r.Results.Count) == 1 &&
                results.Select(r => r.Session.SessionId).Distinct().Count() == 1,
                "concurrent repeated Envelope executes one Intent and one Knowledge query");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            check(Convert.ToInt32(command.ExecuteScalar()) == 1, "Knowledge integration preserves SQLite schema version 1");
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var names = new List<string>(); while (reader.Read()) names.Add(reader.GetString(0));
            check(names.SequenceEqual(["Messages", "Sessions", "StateHistory"]), "no Knowledge execution-history SQLite tables are added");
            check(File.ReadAllText(f.Workflow.ItemsPath) == beforeWorkflow, "concurrent runtime does not mutate Knowledge publication");
            f.Pipeline.Dispose();
            try { await f.Pipeline.ProcessMessageAsync(envelope); check(false, "disposed pipeline rejected"); }
            catch (ObjectDisposedException) { check(true, "disposed Knowledge pipeline rejects execution"); }
        }
        using (var f = New("cancelled"))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await f.Pipeline.ProcessMessageAsync(Envelope("cancelled", "材质", 1), Ocr(ProductA),
                cancellationToken: cancelled.Token); check(false, "cancelled request rejected"); }
            catch (OperationCanceledException)
            {
                check(f.Pipeline.KnowledgeCalls == 0 && f.Handler.Calls == 0 &&
                    new SQLiteAgentSessionStore(f.Database).LoadSessions().Count == 0,
                    "pre-cancelled processing executes neither Context append, Intent nor Knowledge");
            }
            Reject<ArgumentException>(() => new AgentKnowledgePipeline(Path.Combine(f.Root, "unstarted.db"),
                f.ProductState, f.ProductFile, ""), check, "new Agent path requires an explicit workflow source and cannot select legacy mode");
        }
        var all = fixtures.SelectMany(f => f.Results).ToArray();
        File.WriteAllText(Path.Combine(root, "knowledge-results.json"), JsonSerializer.Serialize(all,
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new
        {
            Version = "1.35.0-B", EvidenceKind = "ControlledHTTPAndLocalPublishedKnowledge",
            RealMultiBuyer = "Pending", IntentHTTPCalls = fixtures.Sum(f => f.Handler.Calls),
            KnowledgeProviderCalls = all.Count(r => r.ProviderCalled),
            KnowledgeAvailable = all.Count(r => r.QueryStatus == KnowledgeStatuses.Available),
            KnowledgeUnavailable = all.Count(r => r.QueryStatus == AgentKnowledgePipeline.KnowledgeUnavailable),
            NoProductContext = all.Count(r => r.QueryStatus == KnowledgeStatuses.MissingProductContext),
            IdentityLevel = "Observed", AiReplyCalls = 0, AutomaticSend = 0, SentMessages = 0,
            CounterBasis = "Actual provider-attempt metadata; runtime has no AI/Prompt/Preview/Send services"
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(path =>
            new[] { "model-context.json", "prompt.txt", "reply-preview.json", "send-request.json" }.Contains(Path.GetFileName(path))),
            "Knowledge integration creates no Prompt, AI model context, Reply Preview or Send request");
        var dependencies = typeof(AgentKnowledgePipeline).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Select(field => field.FieldType.Name).ToArray();
        check(!dependencies.Any(name => name.Contains("AIProvider") || name.Contains("Reply") ||
            name.Contains("Dispatch") || name.Contains("Send")), "Knowledge pipeline has no AI Reply or Send dependencies");
        Console.WriteLine("V1350B_CONTROLLED_EVIDENCE: " + root);
    }
}
