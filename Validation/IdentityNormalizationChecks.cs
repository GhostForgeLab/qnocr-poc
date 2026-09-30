using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QnocrPoc.Adapter;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class IdentityNormalizationChecks
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixtureSource(params MessageEnvelope[] envelopes) : IMessageSource
    {
        public string Source => "Normalization.Synthetic";
        public async IAsyncEnumerable<MessageEnvelope> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
        }
    }

    private static readonly DateTime At = new(2026, 9, 30, 18, 0, 0, DateTimeKind.Unspecified);
    private static MessageEnvelope Envelope(string buyer, string text, int second = 0) =>
        new("Normalization.Synthetic", "Seller.Case-Sensitive", buyer, "",
            [new("buyer", text, At.AddSeconds(second))]);

    private static void Sql(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); } catch (T) { check(true, description); return; }
            check(false, description);
        }

        var normalizer = new IdentityNormalizer();
        var provider = new AgentMessageIdentityProvider();
        var vectors = new (string Input, string Expected)[]
        {
            ("张三", "张三"), (" 张 三 ", "张三"), ("张\t三\r\n", "张三"),
            ("张\u00a0\u3000三", "张三"), ("张三.", "张三"), ("张三！", "张三"),
            ("张三,", "张三"), ("张三，。！？", "张三"), ("张 三 ! \t", "张三"),
            ("Tom", "tom"), ("tom", "tom"), ("TOM", "tom"), ("Ｔｏｍ！", "tom"),
            ("Cafe\u0301", "café"), ("CAFÉ", "café")
        };
        var message = new EnvelopeMessage("buyer", "same message", At);
        foreach (var (input, expected) in vectors)
        {
            var normalized = normalizer.Normalize(input);
            check(normalized == expected, $"Observed label normalization: {JsonSerializer.Serialize(input)} -> {expected}");
            var identity = new SessionIdentity("source", "seller", normalized);
            check(provider.GetMessageId(identity, message) ==
                provider.GetMessageId(identity with { BuyerId = expected }, message),
                "normalized alias reaches the unchanged Stable Identity provider with the same identity");
            check(normalizer.Normalize(normalized) == normalized && identity.IdentityLevel == IdentityLevel.Observed,
                "normalization is idempotent and remains Observed");
        }
        foreach (var (left, right) in new[]
        {
            ("张三", "李四"), ("user0", "userO"), ("user1", "userl"), ("user1", "userI"),
            ("a.b", "ab"), ("a_b", "ab"), ("a-b", "ab"), (".tom", "tom"),
            ("tom_", "tom"), ("tom-", "tom"), ("buyer😀", "buyer😁")
        })
            check(normalizer.Normalize(left) != normalizer.Normalize(right),
                $"distinct labels remain distinct: {left} / {right}");
        check(normalizer.Normalize("买家🧪") == "买家🧪", "supplementary Unicode characters are preserved");
        Reject<ArgumentNullException>(() => normalizer.Normalize(null!), "null label is rejected");
        foreach (var invalid in new[] { "", " \t\r\n", "！。，", "\ud800" })
            Reject<ArgumentException>(() => normalizer.Normalize(invalid), "empty, punctuation-only or malformed Unicode label is rejected");

        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1340B", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "isolation.db");
        var clock = new FixedClock();
        var a1 = Envelope("张三", "A1");
        var a2 = Envelope("张 三！", "A2", 1);
        var b1 = Envelope("李四", "B1");
        var b2 = Envelope("李\t四,", "B2", 1);
        AgentSession a, b;
        using (var pipeline = new AgentContextPipeline(database, clock))
        {
            var firstA = pipeline.ProcessMessage(a1);
            var firstB = pipeline.ProcessMessage(b1);
            a = pipeline.ProcessMessage(a2);
            b = pipeline.ProcessMessage(b2);
            check(a.SessionId == firstA.SessionId && b.SessionId == firstB.SessionId,
                "A2 and B2 aliases append to their own A1 and B1 Sessions");
            check(a.SessionId != b.SessionId && a.Identity.BuyerId == "张三" && b.Identity.BuyerId == "李四",
                "buyer A and buyer B have different canonical identities and SessionIds");
            check(a.Messages.Select(m => m.Content).SequenceEqual(new[] { "A1", "A2" }) &&
                b.Messages.Select(m => m.Content).SequenceEqual(new[] { "B1", "B2" }),
                "Session A contains only A1/A2 and Session B only B1/B2");
            check(a.Messages.All(m => m.SessionId == a.SessionId) && b.Messages.All(m => m.SessionId == b.SessionId),
                "every message belongs to its own buyer Session");
            check(a.Messages[1].MessageId == provider.GetMessageId(a.Identity, a2.Messages[0]),
                "Context normalizes before the existing Provider fingerprints the message");
            check(a2.BuyerId == "张 三！" && a2.Messages[0].Text == "A2" && a2.Messages[0].Timestamp.Kind == DateTimeKind.Unspecified,
                "the caller's original Envelope, text and displayed timestamp remain intact");
            check(a.Identity.Source == a1.Source && a.Identity.SellerId == a1.SellerId,
                "source and seller scope keep their exact original values");
            check(provider.GetMessageId(a.Identity, message) != provider.GetMessageId(b.Identity, message),
                "identical message text/time across buyers has different stable fingerprints");

            Sql(database, "CREATE TRIGGER reject_alias_save BEFORE UPDATE ON Sessions BEGIN SELECT RAISE(ABORT, 'unexpected duplicate save'); END;");
            foreach (var alias in new[] { "张三", "张 三", "张三！", "张三." })
                check(ReferenceEquals(pipeline.ProcessMessage(a1 with { BuyerId = alias }), a),
                    "alias retry returns the same Context without refreshing activity or writing SQLite");
            check(ReferenceEquals(pipeline.ProcessMessage(b1 with { BuyerId = "李 四！" }), b),
                "buyer B duplicate is independently idempotent");
            Sql(database, "DROP TRIGGER reject_alias_save;");

            var persisted = new SQLiteAgentSessionStore(database).LoadSessions();
            check(persisted.Count == 2 && persisted.Sum(s => s.Messages.Count) == 4,
                "SQLite persists exactly two buyer Sessions and four messages");
            Reject<ArgumentException>(() => pipeline.ProcessMessage(a1 with { BuyerId = "！ 。" }),
                "empty normalized identity is rejected before any Session mutation");
            Reject<ArgumentException>(() => pipeline.ProcessMessage(a1 with { BuyerId = "\ud800" }),
                "malformed Unicode identity is rejected before any Session mutation");
            Reject<ArgumentNullException>(() => pipeline.ProcessMessage(null!), "null Envelope is rejected");
            check(ReferenceEquals(pipeline.GetSession(a.SessionId), a) &&
                new SQLiteAgentSessionStore(database).LoadSessions().Count == 2,
                "rejected identities leave Context and persisted isolation unchanged");
            check(persisted.All(s => s.Identity.IdentityLevel == IdentityLevel.Observed &&
                s.CurrentProductContext is null && s.IntentHistory.Count == 0),
                "normalization and persistence never upgrade Observed or enrich business state");
        }
        using (var restarted = new AgentContextPipeline(database, clock))
        {
            check(restarted.GetSession(a.SessionId)!.Messages.SequenceEqual(a.Messages) &&
                restarted.GetSession(b.SessionId)!.Messages.SequenceEqual(b.Messages),
                "SQLite restart restores both buyers with original IDs and separate message history");
            check(restarted.ProcessMessage(a1 with { BuyerId = "张 三。" }).SessionId == a.SessionId &&
                restarted.ProcessMessage(b1 with { BuyerId = "李 四！" }).SessionId == b.SessionId,
                "aliases after restart resolve to their own recovered Sessions");
            var recovered = new SQLiteAgentSessionStore(database).LoadSessions();
            check(recovered.Count == 2 && recovered.Sum(s => s.Messages.Count) == 4 &&
                recovered.All(s => s.Identity.IdentityLevel == IdentityLevel.Observed),
                "restart alias retries create no extra Sessions/messages and remain Observed");
        }

        using (var pipeline = new AgentContextPipeline(Path.Combine(root, "boundary.db"), clock))
        {
            var contexts = new List<AgentSession>();
            await foreach (var context in pipeline.ReadAsync(new FixtureSource(
                Envelope("Tom", "  TOM，body! \t"), Envelope("ＴＯＭ！", "  TOM，body! \t"))))
                contexts.Add(context);
            check(contexts.Count == 2 && ReferenceEquals(contexts[0], contexts[1]) && contexts[0].Messages.Count == 1,
                "streamed case/full-width aliases use the same normalization boundary and deduplicate");
            check(contexts[0].Messages[0].Content == "  TOM，body! \t",
                "identity normalization preserves message whitespace, punctuation and case");
            var canonical = contexts[0];
            var otherSeller = pipeline.ProcessMessage(Envelope("tom", "new") with { SellerId = "seller.case-sensitive" });
            var otherSource = pipeline.ProcessMessage(Envelope("tom", "new") with { Source = "normalization.synthetic" });
            check(new[] { canonical.SessionId, otherSeller.SessionId, otherSource.SessionId }.Distinct().Count() == 3,
                "same normalized buyer in another seller or source stays isolated, including case differences");
            var accented = pipeline.ProcessMessage(Envelope("Cafe\u0301", "unicode"));
            check(ReferenceEquals(accented, pipeline.ProcessMessage(Envelope("CAFÉ", "unicode"))),
                "composed/decomposed Unicode aliases share a Session and stable message identity end to end");
            foreach (var (left, right) in new[] { ("user0", "userO"), ("user1", "userl") })
                check(pipeline.ProcessMessage(Envelope(left, "same")).SessionId !=
                    pipeline.ProcessMessage(Envelope(right, "same")).SessionId,
                    "digit/letter OCR lookalikes retain separate Sessions");
        }
        Console.WriteLine($"NORMALIZATION_ISOLATION: sessions=2; messages=4; trust=Observed; AI/Intent/Knowledge/Reply/SentMessages=0; Database={database}");
    }
}
