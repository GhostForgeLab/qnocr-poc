using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QnocrPoc.Adapter;
using QnocrPoc.Decision;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class AgentContextIntentChecks
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 4, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) { _now += elapsed; _ticks += elapsed.Ticks; }
    }

    // Controlled HTTP transport; the real factory, JEV serializer/parser and fallback remain in use.
    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public string Mode { get; set; } = "success";
        public Action? BeforeResponse { get; set; }
        public string Choice { get; set; } = "MATERIAL_QUERY";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                .RootElement.Clone());
            BeforeResponse?.Invoke();
            if (Mode == "throw") throw new HttpRequestException("controlled transport exception");
            if (Mode == "timeout") throw new TaskCanceledException("controlled timeout");
            if (Mode == "http") return new(HttpStatusCode.ServiceUnavailable);
            if (Mode == "invalid") return new(HttpStatusCode.OK)
                { Content = new StringContent("{\"answers\":{}}", Encoding.UTF8, "application/json") };
            var names = new[] { "PRODUCT_SPEC_QUERY", "MATERIAL_QUERY", "SHIPPING_QUERY",
                "CUSTOMIZATION_QUERY", "PRICE_QUERY", "AFTER_SALES_QUERY", "GIFT_QUERY",
                "COLLECTION_QUERY", "OTHER" };
            var answer = JsonSerializer.Serialize(new
            {
                model = "jev-v1350a-controlled",
                answers = new { intent = new { type = "choice", choice = Choice, confidence = 1.0,
                    probabilities = names.ToDictionary(name => name, name => name == Choice ? 1.0 : 0.0) } }
            });
            return new(HttpStatusCode.OK)
                { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly DateTime MessageTime = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Unspecified);
    private static MessageEnvelope Envelope(string buyer, string text, int sequence, string role = "buyer") =>
        new("Qianniu.OCR", "controlled-seller", buyer, "unused-product",
            [new EnvelopeMessage(role, text, MessageTime.AddSeconds(sequence))]);
    private static JsonElement State(Handler handler, int index) => handler.Requests[index].GetProperty("state");
    private static string Current(Handler handler, int index) => State(handler, index).GetProperty("currentBuyerMessage").GetString()!;
    private static string[] Recent(Handler handler, int index) => State(handler, index).GetProperty("recentMessages")
        .EnumerateArray().Select(item => item.GetProperty("text").GetString()!).ToArray();
    private static void Reject<T>(Action action, Action<bool, string> check, string description) where T : Exception
    {
        try { action(); } catch (T) { check(true, description); return; }
        check(false, description);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1350A", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var clock = new Clock();
        var config = Path.GetFullPath("Config/intent-provider.json");
        check(IntentProviderConfig.Load(config).Provider == "JEV",
            "Agent integration reuses the existing default JEV runtime configuration");
        var handler = new Handler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var database = Path.Combine(root, "sessions.db");
        var firstEnvelope = Envelope("buyer-A", "这个酒壶是什么材质", 1);
        AgentSession savedA, savedB;
        var receipts = new List<AgentIntentMessageResult>();
        using (var pipeline = new AgentIntentPipeline(database, config, clock, jevClient: client, apiKey: () => "controlled-key"))
        {
            handler.BeforeResponse = () => check(new SQLiteAgentSessionStore(database).LoadSessions().Sum(s => s.Messages.Count) > 0,
                "Context is durable before the existing JEV HTTP request completes");
            var first = pipeline.ProcessMessage(firstEnvelope);
            handler.BeforeResponse = null;
            receipts.AddRange(first.Results);
            savedA = first.Session;
            check(first.AppendedMessages == 1 && first.Results.Count == 1 && handler.Requests.Count == 1,
                "one new Context buyer message executes Intent exactly once");
            var result = first.Results.Single();
            check(result.Error is null && result.Provider == "JEV" && !result.FallbackUsed &&
                result.Intent?.RuleVersion == "JEV/jev-v1350a-controlled" && result.Intent.Confidence == 1,
                "normal path uses the actual existing JEV provider/parser through IIntentClassifier");
            check(result.SessionId == savedA.SessionId && result.MessageId == savedA.Messages[0].MessageId &&
                result.Timestamp != default && result.LatencyMilliseconds >= 0,
                "Intent result carries the accepted SessionId, stable MessageId, timestamp and measured latency");
            check(Current(handler, 0) == firstEnvelope.Messages[0].Text && Recent(handler, 0).Length == 0,
                "first JEV state contains the current accepted message and an empty recent window");
            check(!State(handler, 0).ToString().Contains("buyer-A") &&
                !State(handler, 0).ToString().Contains("unused-product") &&
                !handler.Requests[0].ToString().Contains("controlled-key"),
                "JEV state sends dialogue only, without observation identity, unbound product or credential");
            clock.Advance(TimeSpan.FromSeconds(1));
            var repeated = pipeline.ProcessMessage(JsonSerializer.Deserialize<MessageEnvelope>(JsonSerializer.Serialize(firstEnvelope))!);
            check(repeated.Results.Count == 0 && repeated.AppendedMessages == 0 && handler.Requests.Count == 1 &&
                ReferenceEquals(savedA, repeated.Session), "serialized Envelope replay reuses Manager dedupe and triggers no Intent");
            var normalizedReplay = pipeline.ProcessMessage(firstEnvelope with { BuyerId = "  buyer-A  " });
            check(normalizedReplay.Session.SessionId == savedA.SessionId && normalizedReplay.Results.Count == 0 &&
                handler.Requests.Count == 1, "existing identity normalization also dedupes Intent without a second identity rule");

            handler.Choice = "PRODUCT_SPEC_QUERY";
            var second = pipeline.ProcessMessage(Envelope("buyer-A", "那200ml的呢", 2));
            savedA = second.Session;
            receipts.AddRange(second.Results);
            check(second.Results.Count == 1 && Current(handler, 1) == "那200ml的呢" &&
                Recent(handler, 1).SequenceEqual(["这个酒壶是什么材质"]),
                "continuous Session delivers the necessary earlier question to the actual JEV request body");
            check(second.Results[0].SessionId == first.Results[0].SessionId &&
                second.Results[0].MessageId != first.Results[0].MessageId,
                "successive results remain on the same Agent Session and different stable messages");
            var seller = pipeline.ProcessMessage(Envelope("buyer-A", "有200ml规格", 3, "seller"));
            check(seller.AppendedMessages == 1 && seller.Results.Count == 0 && handler.Requests.Count == 2,
                "seller dialogue is saved as Context without triggering buyer Intent");
            var third = pipeline.ProcessMessage(Envelope("buyer-A", "那价格呢", 4));
            savedA = third.Session;
            receipts.AddRange(third.Results);
            check(Recent(handler, 2).SequenceEqual(["这个酒壶是什么材质", "那200ml的呢", "有200ml规格"]),
                "bounded recent dialogue includes buyer and seller turns in append order");
            var b = pipeline.ProcessMessage(Envelope("buyer-B", "B独立发货问题", 5));
            savedB = b.Session;
            receipts.AddRange(b.Results);
            check(b.Session.SessionId != savedA.SessionId && Recent(handler, 3).Length == 0 &&
                b.Results[0].SessionId == savedB.SessionId && b.Results[0].MessageId == savedB.Messages[0].MessageId,
                "synthetic buyer B has an independent Intent input and result association");
            var fourth = pipeline.ProcessMessage(Envelope("buyer-A", "A继续问规格", 6));
            savedA = fourth.Session;
            receipts.AddRange(fourth.Results);
            check(Recent(handler, 4).SequenceEqual(["那200ml的呢", "有200ml规格", "那价格呢"]) &&
                !State(handler, 4).ToString().Contains("B独立"), "return to A uses only A's last three turns");
            check(savedA.Identity.IdentityLevel == IdentityLevel.Observed && savedB.Identity.IdentityLevel == IdentityLevel.Observed &&
                savedA.CurrentProductContext is null && savedA.IntentHistory.Count == 0,
                "Intent leaves identity Observed and reserved Agent business/history fields unchanged");
            var missingId = "missing";
            Reject<InvalidOperationException>(() => AgentContextIntentAdapter.Build(savedA, missingId, clock.GetUtcNow()),
                check, "adapter rejects a message absent from the supplied Session");
            Reject<InvalidDataException>(() => AgentContextIntentAdapter.Build(savedA with { Status = AgentSessionStatus.Archived },
                savedA.Messages[^1].MessageId, clock.GetUtcNow()), check, "adapter rejects Archived Session input");
            Reject<InvalidDataException>(() => AgentContextIntentAdapter.Build(savedA with { Status = AgentSessionStatus.Inactive },
                savedA.Messages[^1].MessageId, clock.GetUtcNow()), check, "adapter rejects Inactive Session input");
            var contaminated = savedA with { Messages = savedA.Messages.Append(savedB.Messages[0]).ToArray() };
            Reject<InvalidDataException>(() => AgentContextIntentAdapter.Build(contaminated, savedA.Messages[^1].MessageId,
                clock.GetUtcNow()), check, "adapter rejects foreign Session messages before classification");
            Reject<InvalidDataException>(() => AgentContextIntentAdapter.Build(savedA, seller.Session.Messages[^1].MessageId,
                clock.GetUtcNow()), check, "adapter rejects seller messages as the Intent target");
            var before = handler.Requests.Count;
            Reject<ArgumentException>(() => pipeline.ProcessMessage(firstEnvelope with { SchemaVersion = 2 }), check,
                "invalid Envelope remains rejected by Context before Intent");
            check(handler.Requests.Count == before, "rejected Context input makes no provider request");
        }
        var stored = new SQLiteAgentSessionStore(database).LoadSessions();
        check(stored.Count == 2 && stored.Single(s => s.SessionId == savedA.SessionId).Messages.SequenceEqual(savedA.Messages),
            "SQLite preserves the original Context message history after Intent");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));
            check(tables.SequenceEqual(["Messages", "Sessions", "StateHistory"]), "Intent introduces no SQLite tables or migration");
            reader.Close();
            command.CommandText = "PRAGMA user_version;";
            check(Convert.ToInt32(command.ExecuteScalar()) == 1, "SQLite schema remains version 1");
        }

        var callsBeforeRestore = handler.Requests.Count;
        using var restoredClient = new HttpClient(handler, disposeHandler: false);
        using (var restored = new AgentIntentPipeline(database, config, clock, jevClient: restoredClient, apiKey: () => "controlled-key"))
        {
            check(handler.Requests.Count == callsBeforeRestore, "component reconstruction does not classify stored history");
            var recovered = restored.GetSession(savedA.SessionId)!;
            check(recovered.Messages.SequenceEqual(savedA.Messages) && recovered.Identity == savedA.Identity,
                "reconstructed Context recovers A's messages and Observed identity");
            check(restored.ProcessMessage(firstEnvelope).Results.Count == 0 &&
                handler.Requests.Count == callsBeforeRestore, "recovered stable message replay is ignored by Intent");
            var next = restored.ProcessMessage(Envelope("buyer-A", "恢复后A问发货", 7));
            receipts.AddRange(next.Results);
            check(next.Session.SessionId == savedA.SessionId && next.Session.Messages.Count == savedA.Messages.Count + 1 &&
                next.Results.Count == 1 && handler.Requests.Count == callsBeforeRestore + 1,
                "new buyer message after SQLite recovery continues Intent on the original Session");
            check(Recent(handler, callsBeforeRestore).SequenceEqual(["有200ml规格", "那价格呢", "A继续问规格"]),
                "post-recovery JEV receives the recovered bounded dialogue");
            var afterB = restored.ProcessMessage(Envelope("buyer-B", "恢复后B问快递", 8));
            receipts.AddRange(afterB.Results);
            check(afterB.Session.SessionId == savedB.SessionId &&
                Recent(handler, callsBeforeRestore + 1).SequenceEqual(["B独立发货问题"]),
                "synthetic B recovery continues separately without A's history");
        }

        await CheckBatchesAndBounds(root, config, check);
        CheckFailures(root, config, check, receipts);
        await CheckSourceAndConcurrency(root, config, check);
        CheckLifecycleAndPriorHistory(root, config, check);
        File.WriteAllText(Path.Combine(root, "intent-results.json"), JsonSerializer.Serialize(receipts,
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new
        {
            Version = "1.35.0-A", EvidenceKind = "ControlledHTTPExistingJEV", RealMultiBuyer = "Pending",
            MainPathJEVSuccess = handler.Requests.Count,
            AverageMainPathLatencyMilliseconds = receipts.Where(r => r.Provider == "JEV").Average(r => r.LatencyMilliseconds),
            IdentityLevel = "Observed", KnowledgeCalls = 0, AiReplyCalls = 0, SentMessages = 0,
            CounterBasis = "Intent-only pipeline has no downstream service dependencies or dispatch"
        }, new JsonSerializerOptions { WriteIndented = true }));
        check(!Directory.EnumerateFiles(root, "*preview*", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(root, "*send-request*", SearchOption.AllDirectories).Any(),
            "Intent-only integration creates no reply preview or send artifacts");
        var fields = typeof(AgentIntentPipeline).GetFields(System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance).Select(f => f.FieldType.FullName ?? "").ToArray();
        check(!fields.Any(name => name.Contains("Knowledge") || name.Contains("Reply") || name.Contains("Dispatch") || name.Contains("Send")),
            "Intent-only pipeline has no Knowledge, AI Reply or Send service dependencies");
        Console.WriteLine("V1350A_CONTROLLED_EVIDENCE: " + root);
    }

    private static async Task CheckBatchesAndBounds(string root, string config, Action<bool, string> check)
    {
        var handler = new Handler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var clock = new Clock();
        using var pipeline = new AgentIntentPipeline(Path.Combine(root, "batch.db"), config, clock,
            jevClient: client, apiKey: () => "controlled-key");
        var first = Envelope("batch", "先问材质", 1);
        var second = Envelope("batch", "再问200ml", 2);
        var combined = first with { Messages = [first.Messages[0], first.Messages[0], second.Messages[0]] };
        var batch = pipeline.ProcessMessage(combined);
        check(batch.AppendedMessages == 2 && batch.Results.Count == 2 && handler.Requests.Count == 2,
            "batch-internal stable duplicates are classified once per appended buyer message");
        check(Recent(handler, 0).Length == 0 && Recent(handler, 1).SequenceEqual(["先问材质"]) &&
            Current(handler, 0) == "先问材质", "earlier batch request excludes later batch messages");
        check(pipeline.ProcessMessage(combined).Results.Count == 0 && handler.Requests.Count == 2,
            "full batch replay triggers no additional Intent");
        var partial = first with { Messages = [first.Messages[0], Envelope("batch", "追加价格", 3).Messages[0]] };
        check(pipeline.ProcessMessage(partial).Results.Count == 1 && handler.Requests.Count == 3,
            "mixed old/new batch classifies only the new message");

        for (var index = 4; index <= 8; index++)
            pipeline.ProcessMessage(Envelope("bounded", "history-" + index, index));
        pipeline.ProcessMessage(Envelope("bounded", "current-bounded", 9));
        var last = handler.Requests.Count - 1;
        check(Recent(handler, last).SequenceEqual(["history-6", "history-7", "history-8"]),
            "longer Session sends exactly the last three eligible prior messages");
        pipeline.ProcessMessage(Envelope("chars", new string('甲', 1000), 10));
        pipeline.ProcessMessage(Envelope("chars", new string('乙', 1000), 11));
        pipeline.ProcessMessage(Envelope("chars", new string('丙', 1000), 12));
        pipeline.ProcessMessage(Envelope("chars", new string('丁', 1000), 13));
        last = handler.Requests.Count - 1;
        check(Recent(handler, last).Sum(t => t.Length) + Current(handler, last).Length == 3000 &&
            Recent(handler, last).All(t => t.Length <= 1000), "actual JEV body respects the 3000-character total dialogue budget");
        var before = handler.Requests.Count;
        var tooLong = pipeline.ProcessMessage(Envelope("chars", "材质" + new string('长', 1001), 14));
        check(handler.Requests.Count == before && tooLong.Results.Single() is { Provider: "RuleBased", FallbackUsed: true,
            FallbackReason: "InvalidDataException", Error: null } && tooLong.Results[0].Intent?.IntentName == "MATERIAL_QUERY",
            "oversized current message remains intact in Context and uses existing fallback without HTTP");
        check(tooLong.Session.Messages[^1].Content.Length == 1003, "Intent bounds do not truncate stored Context content");

        var valid = AgentContextIntentAdapter.Build(batch.Session, batch.Session.Messages[^1].MessageId, clock.GetUtcNow());
        using var windowClient = new HttpClient(handler, disposeHandler: false);
        using var selected = IntentClassifierFactory.Create(config, jevClient: windowClient, apiKey: () => "controlled-key");
        before = handler.Requests.Count;
        var invalidWindow = valid with { IntentRecentMessages = Enumerable.Repeat(new EnvelopeMessage("buyer", "x", MessageTime), 4).ToArray() };
        check(selected.Classifier.Classify(invalidWindow).RuleVersion == "V1.15" && handler.Requests.Count == before,
            "existing JEV provider rejects an oversized opt-in recent window before HTTP and falls back");
        var source = new OcrMessageSource(_ => Task.FromResult(new BuyerMessageBatch("controlled-only", "",
            [new BuyerMessage { Text = "source问材质", Timestamp = MessageTime }], [], [], "None", "Controlled fixture")),
            "controlled-seller", "source-buyer");
        var read = new List<AgentIntentBatchResult>();
        await foreach (var item in pipeline.ReadAsync(source)) read.Add(item);
        check(read.Count == 1 && read[0].Results.Count == 1 && read[0].Session.Identity.Source == "Qianniu.OCR",
            "existing OcrMessageSource flows through Agent Context into Intent without changing MessageEnvelope");
    }

    private static void CheckFailures(string root, string config, Action<bool, string> check,
        List<AgentIntentMessageResult> receipts)
    {
        foreach (var mode in new[] { "http", "throw", "timeout", "invalid" })
        {
            var handler = new Handler { Mode = mode };
            using var client = new HttpClient(handler, disposeHandler: false);
            using var pipeline = new AgentIntentPipeline(Path.Combine(root, mode + ".db"), config, new Clock(),
                jevClient: client, apiKey: () => "controlled-key");
            var envelope = Envelope(mode, "这个酒壶是什么材质", 1);
            var failed = pipeline.ProcessMessage(envelope);
            var result = failed.Results.Single();
            receipts.Add(result);
            check(result.Provider == "RuleBased" && result.FallbackUsed && result.Error is null &&
                result.Intent?.IntentName == "MATERIAL_QUERY" && !string.IsNullOrWhiteSpace(result.FallbackReason),
                mode + " JEV failure uses the existing RuleBased fallback with a recorded reason");
            check(failed.Session.Messages.Count == 1 && handler.Requests.Count == 1 &&
                pipeline.ProcessMessage(envelope).Results.Count == 0 && handler.Requests.Count == 1,
                mode + " failure preserves Context and does not retry a duplicate Intent");
            handler.Mode = "success";
            var recovered = pipeline.ProcessMessage(Envelope(mode, "恢复后材质", 2)).Results.Single();
            check(recovered.Provider == "JEV" && !recovered.FallbackUsed && recovered.FallbackReason is null,
                mode + " transient failure does not disable JEV for the next message");
        }
        var unavailable = new Handler();
        using var unavailableClient = new HttpClient(unavailable);
        using (var pipeline = new AgentIntentPipeline(Path.Combine(root, "no-key.db"), config, new Clock(),
            jevClient: unavailableClient, apiKey: () => null))
        {
            var result = pipeline.ProcessMessage(Envelope("no-key", "什么时候发货", 1)).Results.Single();
            receipts.Add(result);
            check(result.Provider == "RuleBased" && result.FallbackUsed && result.FallbackReason == "ApiKeyMissing" &&
                result.Intent?.IntentName == "SHIPPING_QUERY" && unavailable.Requests.Count == 0,
                "JEV unavailable at startup uses existing fallback without making an HTTP request");
        }
        var ruleConfig = Path.Combine(root, "rule.json");
        File.WriteAllText(ruleConfig, JsonSerializer.Serialize(IntentProviderConfig.Load(config) with { Provider = "RuleBased" }));
        using (var pipeline = new AgentIntentPipeline(Path.Combine(root, "rule.db"), ruleConfig, new Clock(),
            jevClient: unavailableClient, apiKey: () => null))
        {
            var result = pipeline.ProcessMessage(Envelope("rule", "什么时候发货", 1)).Results.Single();
            check(result.Provider == "RuleBased" && !result.FallbackUsed && result.FallbackReason is null,
                "explicit RuleBased selection preserves the existing rollback configuration");
        }
        var failingDatabase = Path.Combine(root, "store-failure.db");
        using (var pipeline = new AgentIntentPipeline(failingDatabase, config, new Clock(),
            jevClient: unavailableClient, apiKey: () => "controlled-key"))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = failingDatabase, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER controlled_save_failure BEFORE INSERT ON Messages BEGIN SELECT RAISE(ABORT, 'controlled'); END;";
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => pipeline.ProcessMessage(Envelope("store", "材质", 1)), check,
                "SQLite save failure stops before Intent and preserves Manager atomicity");
            check(unavailable.Requests.Count == 0, "failed Context persistence executes no Intent request");
            command.CommandText = "DROP TRIGGER controlled_save_failure;";
            command.ExecuteNonQuery();
            check(pipeline.ProcessMessage(Envelope("store", "材质", 1)).Results.Count == 1 && unavailable.Requests.Count == 1,
                "message rejected by persistence can be accepted and classified after the store is repaired");
        }
    }

    private static async Task CheckSourceAndConcurrency(string root, string config, Action<bool, string> check)
    {
        var handler = new Handler();
        using var client = new HttpClient(handler, disposeHandler: false);
        using var pipeline = new AgentIntentPipeline(Path.Combine(root, "concurrent.db"), config, new Clock(),
            jevClient: client, apiKey: () => "controlled-key");
        var envelope = Envelope("parallel", "材质", 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => pipeline.ProcessMessage(envelope))));
        check(handler.Requests.Count == 1 && results.Sum(r => r.Results.Count) == 1 &&
            results.Select(r => r.Session.SessionId).Distinct().Count() == 1,
            "concurrent duplicate submissions execute one Intent on one Session");
        pipeline.Dispose();
        Reject<ObjectDisposedException>(() => pipeline.ProcessMessage(envelope), check, "disposed Intent pipeline rejects further messages");
    }

    private static void CheckLifecycleAndPriorHistory(string root, string config, Action<bool, string> check)
    {
        var handler = new Handler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var clock = new Clock();
        var database = Path.Combine(root, "prior-context.db");
        AgentSession prior;
        using (var context = new AgentContextPipeline(database, clock))
            prior = context.ProcessMessage(Envelope("prior", "Context-only旧材质", 1));
        using (var pipeline = new AgentIntentPipeline(database, config, clock, jevClient: client, apiKey: () => "controlled-key"))
        {
            check(pipeline.ProcessMessage(Envelope("prior", "Context-only旧材质", 1)).Results.Count == 0 &&
                handler.Requests.Count == 0, "enabling Intent does not backfill existing Context-only history");
            var next = pipeline.ProcessMessage(Envelope("prior", "新问200ml", 2));
            check(next.Session.SessionId == prior.SessionId && Recent(handler, 0).SequenceEqual(["Context-only旧材质"]),
                "new message can use pre-integration recovered Context as bounded supporting history");
            clock.Advance(TimeSpan.FromHours(26));
            var fresh = pipeline.ProcessMessage(Envelope("prior", "新的会话发货", 3));
            check(fresh.Session.SessionId != prior.SessionId && Recent(handler, 1).Length == 0 &&
                pipeline.GetSession(prior.SessionId)?.Status == AgentSessionStatus.Archived,
                "new lifecycle Session never borrows the archived Session's dialogue");
        }
    }
}
