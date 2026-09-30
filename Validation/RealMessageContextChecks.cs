using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QnocrPoc.Adapter;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class RealMessageContextChecks
{
    private static bool Equal(AgentSession? actual, AgentSession expected) => actual is not null &&
        actual.SessionId == expected.SessionId && actual.Identity == expected.Identity &&
        actual.Status == expected.Status && actual.CreatedTime == expected.CreatedTime &&
        actual.LastActiveTime == expected.LastActiveTime && actual.Messages.SequenceEqual(expected.Messages) &&
        actual.Messages.Select(m => m.Timestamp.Kind).SequenceEqual(expected.Messages.Select(m => m.Timestamp.Kind)) &&
        actual.StateHistory.SequenceEqual(expected.StateHistory);

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static int RunChild(string database, string manifest)
    {
        var expected = JsonSerializer.Deserialize<AgentSession[]>(File.ReadAllText(manifest))!;
        using var pipeline = new AgentContextPipeline(database);
        foreach (var session in expected)
            if (!Equal(pipeline.GetSession(session.SessionId), session)) return 1;
        Console.WriteLine($"RESTORED: sessions={expected.Length}; messages={expected.Sum(s => s.Messages.Count)}; AI/Intent/Knowledge/Reply/SentMessages=0");
        return 0;
    }

    private static async Task VerifyChildAsync(string database, AgentSession[] sessions, string manifest,
        Action<bool, string> check)
    {
        File.WriteAllText(manifest, JsonSerializer.Serialize(sessions));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing check apphost.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--real-message-context-child");
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(manifest);
        using var child = Process.Start(start) ?? throw new IOException("Restore child failed to start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await child.WaitForExitAsync(deadline.Token); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        var output = await stdout;
        var error = await stderr;
        File.WriteAllText(manifest + ".child.log", output + error);
        check(child.ExitCode == 0 && output.Contains("RESTORED:", StringComparison.Ordinal),
            "independent Context process restores every Session, message, identity, timestamp and lifecycle field");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); } catch (T) { check(true, description); return; }
            check(false, description);
        }
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1340A", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "sessions.db");
        var at = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified);
        // These are integration fixtures; live OCR evidence is checked separately below.
        var messages = new[]
        {
            new BuyerMessage { Text = "synthetic context one", Timestamp = at },
            new BuyerMessage { Text = "synthetic context two", Timestamp = at.AddSeconds(1) }
        };
        var batch = new BuyerMessageBatch("synthetic-only", "", messages, [], [], "None", "Synthetic integration fixture");
        IMessageSource source = new OcrMessageSource(_ => Task.FromResult(batch), "seller-fixture", "buyer-fixture");
        var envelope = new MessageEnvelope("Qianniu.OCR", "seller-fixture", "buyer-fixture", "",
            messages.Select(m => new EnvelopeMessage(m.Role, m.Text, m.Timestamp)).ToArray());
        var provider = new AgentMessageIdentityProvider();
        var identity = new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId);
        AgentSession saved;
        using (var pipeline = new AgentContextPipeline(database))
        {
            var observed = new List<AgentSession>();
            await foreach (var context in pipeline.ReadAsync(source)) observed.Add(context);
            check(observed.Count == 1 && observed[0].Messages.Count == 2,
                "unchanged OcrMessageSource feeds the existing MessageEnvelope into Context pipeline");
            saved = observed.Single();
            check(saved.Identity == identity && saved.Identity.IdentityLevel == IdentityLevel.Observed,
                "Context session preserves exact source/seller/buyer and Observed trust");
            check(saved.Messages.Select(m => m.MessageId).SequenceEqual(envelope.Messages.Select(m => provider.GetMessageId(identity, m))),
                "pipeline uses the existing Stable Identity provider for every message");
            check(saved.Messages.Select(m => m.MessageId).Distinct().Count() == 2,
                "different input messages have different stable IDs");
            check(Equal(new SQLiteAgentSessionStore(database).GetSession(saved.SessionId), saved),
                "SQLite already contains the complete snapshot when the pipeline returns");
            Sql(database, "CREATE TRIGGER reject_context_update BEFORE UPDATE ON Sessions BEGIN SELECT RAISE(ABORT, 'unexpected duplicate save'); END;");
            var repeated = pipeline.ProcessMessage(JsonSerializer.Deserialize<MessageEnvelope>(JsonSerializer.Serialize(envelope))!);
            check(ReferenceEquals(saved, repeated), "fresh duplicate DTO preserves Context and performs no SQLite save");
            check(Equal(new SQLiteAgentSessionStore(database).GetSession(saved.SessionId), saved),
                "duplicate input does not alter SQLite messages or LastActiveTime");
            Sql(database, "DROP TRIGGER reject_context_update;");
            var next = envelope with { Messages = [.. envelope.Messages, new("buyer", "synthetic third", at.AddSeconds(2))] };
            var appended = pipeline.ProcessMessage(next);
            check(appended.SessionId == saved.SessionId && appended.Messages.Count == 3,
                "overlapping batch appends only its new message to the same Session");
            check(appended.Messages.Take(2).SequenceEqual(saved.Messages), "append preserves prior IDs and accepted order");
            saved = appended;
            foreach (var other in new[]
            {
                envelope with { BuyerId = "other-buyer" }, envelope with { SellerId = "other-seller" },
                envelope with { Source = "Other.ReadOnly" }
            })
            {
                var isolated = pipeline.ProcessMessage(other);
                check(isolated.SessionId != saved.SessionId && isolated.Messages.Count == 2 &&
                    isolated.Identity == new SessionIdentity(other.Source, other.SellerId, other.BuyerId),
                    "buyer, seller and source changes create isolated SQLite Sessions");
            }
            check(pipeline.GetSession(saved.SessionId)!.Messages.Count == 3,
                "isolated inputs do not pollute the original Session");
            var before = new SQLiteAgentSessionStore(database).LoadSessions().Count;
            Reject<ArgumentException>(() => pipeline.ProcessMessage(envelope with { BuyerId = "" }),
                "unknown buyer identity is rejected before any Session save");
            Reject<ArgumentException>(() => pipeline.ProcessMessage(envelope with { Messages = [new("buyer", "incomplete", default)] }),
                "incomplete message is rejected before any Session save");
            check(new SQLiteAgentSessionStore(database).LoadSessions().Count == before,
                "rejected envelopes do not create SQLite Sessions");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellationObserved = false;
            try { await foreach (var unused in pipeline.ReadAsync(source, cancelled.Token)) { } }
            catch (OperationCanceledException) { cancellationObserved = true; }
            check(cancellationObserved && Equal(pipeline.GetSession(saved.SessionId), saved),
                "cancelled source stops without writing or refreshing Context");
        }
        using (var restarted = new AgentContextPipeline(database))
        {
            check(Equal(restarted.GetSession(saved.SessionId), saved), "new Context components restore the original Session snapshot");
            var restoredRetry = restarted.ProcessMessage(envelope);
            check(Equal(restoredRetry, saved), "restarted Context deduplicates pre-restart messages");
            var appended = restarted.ProcessMessage(envelope with { Messages = [new("buyer", "synthetic after restart", at.AddSeconds(3))] });
            check(appended.SessionId == saved.SessionId && appended.Messages.Count == 4,
                "new message after restart appends to the recovered Session");
        }
        var sessions = new SQLiteAgentSessionStore(database).LoadSessions().ToArray();
        await VerifyChildAsync(database, sessions, Path.Combine(root, "expected-sessions.json"), check);
        var disposed = new AgentContextPipeline(database);
        disposed.Dispose();
        Reject<ObjectDisposedException>(() => disposed.ProcessMessage(envelope), "disposed Context rejects input");
        check(sessions.All(s => s.CurrentProductContext is null && s.IntentHistory.Count == 0),
            "Context-only integration does not populate product or Intent state");
        check(await AgentContextCaptureCommand.RunAsync(["--agent-context-capture"]) == 2,
            "invalid capture command fails before touching desktop or database");
    }

    public static async Task RunEvidenceAsync(string directory, Action<bool, string> check)
    {
        directory = Path.GetFullPath(directory);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session.json")));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
        var database = metadata.RootElement.GetProperty("DatabasePath").GetString()!;
        check(summary.RootElement.GetProperty("Status").GetString() == "Captured", "bounded live capture completed with controlled messages");
        check(new[] { "AiCalls", "IntentCalls", "KnowledgeCalls", "ReplyCalls", "SentMessages" }
            .All(key => summary.RootElement.GetProperty(key).GetInt32() == 0) &&
            !summary.RootElement.GetProperty("SendEnabled").GetBoolean(),
            "live Context path reports AI=0, Intent=0, Knowledge=0, Reply=0, SentMessages=0 and send disabled");
        var prefix = metadata.RootElement.GetProperty("TestPrefix").GetString()!;
        var provider = new AgentMessageIdentityProvider();
        var envelopeFiles = Directory.GetFiles(Path.Combine(directory, "frames"), "envelope_*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        check(envelopeFiles.Length > 0 && envelopeFiles.Length == summary.RootElement.GetProperty("Envelopes").GetInt32(),
            "live MessageEnvelope evidence count matches capture summary");
        var envelopes = envelopeFiles.Select(path => JsonSerializer.Deserialize<MessageEnvelope>(File.ReadAllText(path))!).ToArray();
        check(envelopes.All(e => e.Source == "Qianniu.OCR" && e.SchemaVersion == 1 &&
            e.Messages.All(m => m.Role == "buyer" && m.Text.StartsWith(prefix, StringComparison.Ordinal))),
            "only marked real OCR buyer messages entered unchanged MessageEnvelope v1");
        var latest = new Dictionary<string, AgentSession>(StringComparer.Ordinal);
        foreach (var path in envelopeFiles)
        {
            var contextPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path).Replace("envelope_", "context_", StringComparison.Ordinal));
            var context = JsonSerializer.Deserialize<AgentSession>(File.ReadAllText(contextPath))!;
            latest[context.SessionId] = context;
        }
        var stored = new SQLiteAgentSessionStore(database).LoadSessions().ToArray();
        check(stored.Length == latest.Count && stored.All(s => latest.TryGetValue(s.SessionId, out var expected) && Equal(s, expected)),
            "SQLite live snapshots match the last observed Context for every Session");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Messages;";
            check((long)command.ExecuteScalar()! == stored.Sum(s => s.Messages.Count),
                "direct SQLite Messages row count matches persisted Context with no extra rows");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in envelopes)
        {
            var identity = new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId);
            var context = stored.Single(s => s.Identity == identity);
            foreach (var message in envelope.Messages)
            {
                var id = provider.GetMessageId(identity, message);
                check(id == new AgentMessageIdentityProvider().GetMessageId(identity,
                    JsonSerializer.Deserialize<EnvelopeMessage>(JsonSerializer.Serialize(message))!),
                    "real message ID is stable across a fresh DTO and provider");
                check(context.Messages.Any(m => m.MessageId == id && m.Content == message.Text &&
                    m.Timestamp.Ticks == message.Timestamp.Ticks && m.Timestamp.Kind == message.Timestamp.Kind),
                    "real message ID, text and displayed timestamp belong to the correct persisted Session");
                ids.Add(id);
            }
        }
        check(ids.Count == stored.Sum(s => s.Messages.Count) && stored.All(s =>
            s.Messages.Select(m => m.MessageId).Distinct(StringComparer.Ordinal).Count() == s.Messages.Count),
            "different real messages have distinct IDs and repeated observations leave no duplicate SQLite rows");
        var logicalMessages = envelopes.SelectMany(envelope => envelope.Messages.Select(message =>
            (envelope.Source, envelope.SellerId, envelope.BuyerId, message.Role,
             message.Timestamp.Ticks, message.Timestamp.Kind,
             Marker: new string(message.Text.TakeWhile(char.IsAsciiDigit).ToArray())))).Distinct().Count();
        check(ids.Count == logicalMessages && ids.Count >= 3,
            "controlled live sample has at least three messages and no OCR revisions producing extra IDs");
        using (var restarted = new AgentContextPipeline(database))
        {
            check(stored.All(s => Equal(restarted.GetSession(s.SessionId), s)), "restarted live Context restores every exact Session snapshot");
            foreach (var envelope in envelopes)
            {
                var context = restarted.ProcessMessage(envelope);
                check(Equal(context, stored.Single(s => s.SessionId == context.SessionId)),
                    "real envelope retry after restart keeps SessionId, message count and activity unchanged");
            }
        }
        await VerifyChildAsync(database, stored, Path.Combine(directory, "expected-recovery.json"), check);
        check(stored.All(s => s.Identity.IdentityLevel == IdentityLevel.Observed && s.CurrentProductContext is null && s.IntentHistory.Count == 0),
            "live Context remains Observed and has no AI, Intent or Knowledge enrichment");
        Console.WriteLine($"LIVE_EVIDENCE: sessions={stored.Length}; distinctMessages={ids.Count}; envelopes={envelopes.Length}; Database={database}");
    }
}