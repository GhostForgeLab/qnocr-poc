using System.Diagnostics;
using Microsoft.Data.Sqlite;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class AgentSessionPersistenceChecks
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly AgentSessionManagerOptions Options = new() { ArchiveAfterIdleTimeout = TimeSpan.FromMinutes(10) };

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan delta) { _now += delta; _timestamp += delta.Ticks; }
        public void CorrectClock(TimeSpan delta) => _now += delta;
    }

    private sealed class FixedProvider(string id) : IAgentMessageIdentityProvider
    {
        public int Calls { get; private set; }
        public string GetMessageId(SessionIdentity identity, EnvelopeMessage message,
            string? sourceMessageId = null, string? fallbackIdentity = null) { Calls++; return id; }
    }

    private static MessageEnvelope Envelope(string buyer = "buyer") =>
        new("Persistence.Synthetic", "seller", buyer, "product",
            [new EnvelopeMessage("buyer", "持久化 synthetic ' ; -- \0 🧪", Start.UtcDateTime)]);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Sql(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(string path, string table)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        // Test-owned table names only.
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }

    private static bool Equal(AgentSession? actual, AgentSession expected) =>
        actual is not null && actual.SessionId == expected.SessionId && actual.Identity == expected.Identity &&
        actual.Status == expected.Status && actual.CreatedTime == expected.CreatedTime &&
        actual.LastActiveTime == expected.LastActiveTime && actual.Messages.SequenceEqual(expected.Messages) &&
        actual.Messages.Select(m => m.Timestamp.Kind).SequenceEqual(expected.Messages.Select(m => m.Timestamp.Kind)) &&
        actual.StateHistory.SequenceEqual(expected.StateHistory);

    public static int RunChild(string path, string sessionId)
    {
        using var manager = new AgentSessionManager(Timeout, new ManualClock(Start),
            new SQLiteAgentSessionStore(path), Options);
        var snapshot = manager.GetSession(sessionId);
        if (snapshot is not { Status: AgentSessionStatus.Active, Messages.Count: 2 } ||
            snapshot.LastActiveTime != Start || snapshot.Identity.IdentityLevel != IdentityLevel.Observed) return 1;
        var repeated = manager.ProcessMessage(Envelope());
        if (!ReferenceEquals(snapshot, repeated) || repeated.Messages.Count != 2) return 2;
        manager.ProcessMessage(Envelope() with { Messages = [new("seller", "child append", Start.UtcDateTime.AddSeconds(2))] });
        return 0;
    }

    public static void Run(Action<bool, string> check)
    {
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, description); return; }
            check(false, description);
        }
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1330C", "databases", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string Database(string name) => Path.Combine(root, name + ".db");
        var provider = new AgentMessageIdentityProvider();
        var envelope = Envelope();
        var identity = new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId);
        var message = envelope.Messages[0];
        var id = provider.GetMessageId(identity, message);
        check(provider.GetMessageId(new SessionIdentity("s", "a", "b"),
            new EnvelopeMessage("buyer", "hello", Start.UtcDateTime)) ==
            "agent:v1:faad29886680abcb1fb9cc0a5a168630be06edec4d2dfd483ab6edeeaff80642",
            "v1 golden identity vector pins the fingerprint format across future releases");
        check(id.StartsWith("agent:v1:", StringComparison.Ordinal) && id.Length == 73,
            "generated IDs carry a version and full SHA-256 fingerprint");
        check(Enumerable.Range(0, 100).All(_ => provider.GetMessageId(identity, message) == id) &&
            new AgentMessageIdentityProvider().GetMessageId(identity, message) == id,
            "100 identical inputs and a fresh provider produce identical IDs");
        check(provider.GetMessageId(identity, message, " Native ID ") == " Native ID " &&
            provider.GetMessageId(identity, message with { Text = "changed" }, " Native ID ", "fallback") == " Native ID ",
            "source message ID has first priority and preserves exact opaque content");
        check(provider.GetMessageId(identity, message, " ") == id,
            "blank source ID falls through to the timestamp/content fingerprint");
        check(provider.GetMessageId(identity, message, fallbackIdentity: "different") == id,
            "complete timestamp/content input outranks a fallback key");
        foreach (var variant in new[]
        {
            message with { Text = message.Text + " " }, message with { Role = "seller" },
            message with { Timestamp = message.Timestamp.AddTicks(1) },
            message with { Timestamp = DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Unspecified) },
            message with { Timestamp = DateTime.SpecifyKind(message.Timestamp, DateTimeKind.Local) }
        })
            check(provider.GetMessageId(identity, variant) != id, "content, role, timestamp ticks and Kind distinguish messages");
        foreach (var variant in new[]
        {
            identity with { Source = "Other" }, identity with { SellerId = "other" },
            identity with { BuyerId = "other" }, identity with { BuyerId = "BUYER" }
        })
            check(provider.GetMessageId(variant, message) != id, "source, seller, buyer and case are separate identity components");
        check(provider.GetMessageId(identity with { SellerId = "a:b", BuyerId = "c" }, message) !=
            provider.GetMessageId(identity with { SellerId = "a", BuyerId = "b:c" }, message),
            "length-prefixed identity components cannot collide on delimiters");
        check(provider.GetMessageId(identity, message with { Text = "\ud800" }) !=
            provider.GetMessageId(identity, message with { Text = "\ud801" }),
            "hashing does not replace distinct unpaired UTF-16 surrogates with the same bytes");
        var fallback = message with { Timestamp = default };
        check(provider.GetMessageId(identity, fallback) == new AgentMessageIdentityProvider().GetMessageId(identity, fallback),
            "fallback without a timestamp stays deterministic across providers");
        check(provider.GetMessageId(identity, fallback) != provider.GetMessageId(identity, fallback with { Text = "other" }),
            "fallback content distinguishes incomplete observations");
        check(provider.GetMessageId(identity, fallback, fallbackIdentity: "capture-A") ==
            provider.GetMessageId(identity, fallback with { Text = "changed" }, fallbackIdentity: "capture-A"),
            "explicit stable fallback key preserves identity despite changing displayed content");
        check(provider.GetMessageId(identity, fallback, fallbackIdentity: "capture-A") !=
            provider.GetMessageId(identity, fallback, fallbackIdentity: "capture-B"),
            "distinct fallback keys distinguish observations");
        check(provider.GetMessageId(identity, fallback) != id, "fallback and timestamp strategies have separate hash domains");
        Reject<ArgumentNullException>(() => provider.GetMessageId(null!, message), "identity provider rejects null identity");
        Reject<ArgumentNullException>(() => provider.GetMessageId(identity, null!), "identity provider rejects null message");
        Reject<ArgumentException>(() => provider.GetMessageId(identity with { Source = "" }, message), "identity provider rejects missing source");

        var clock = new ManualClock(Start);
        using (var manager = new AgentSessionManager(Timeout, clock))
        {
            var first = manager.ProcessMessage(envelope);
            clock.Advance(TimeSpan.FromMinutes(1));
            check(ReferenceEquals(first, manager.ProcessMessage(envelope)) && first.Messages.Count == 1,
                "default envelope ingestion now deduplicates identical retries without caller IDs");
            check(first.LastActiveTime == Start, "generated-ID retries do not refresh activity");
            var batch = manager.ProcessMessage(envelope with
            {
                Messages = [message, message with { Text = "second" }, message with { Text = "second" }]
            });
            check(batch.Messages.Count == 2 && batch.Messages[1].Content == "second",
                "generated identity deduplicates mixed and repeated batch entries");
            var concurrentIds = new string[64];
            Parallel.For(0, 64, i => concurrentIds[i] = manager.ProcessMessage(envelope).SessionId);
            check(concurrentIds.Distinct().Count() == 1 && manager.GetSession(first.SessionId)!.Messages.Count == 2,
                "64 concurrent default-ingestion retries preserve one bounded history");
            Reject<ArgumentException>(() => manager.ProcessMessage(envelope with { Messages = [fallback] }),
                "fallback identity availability does not relax the complete-message ingestion contract");
        }
        var injected = new FixedProvider("injected-id");
        using (var manager = new AgentSessionManager(messageIdentityProvider: injected))
        {
            check(manager.ProcessMessage(envelope).Messages[0].MessageId == "injected-id" && injected.Calls == 1,
                "manager accepts an independent message identity provider");
            manager.ProcessMessage(envelope, ["native-id"]);
            check(injected.Calls == 1, "explicit source IDs bypass fingerprint generation");
        }
        using (var invalid = new AgentSessionManager(messageIdentityProvider: new FixedProvider("")))
            Reject<ArgumentException>(() => invalid.ProcessMessage(envelope), "blank generated identity is rejected before storage mutation");

        var path = Database("restart");
        var store = new SQLiteAgentSessionStore(path);
        check(File.Exists(path) && store.LoadSessions().Count == 0, "SQLite initializes an empty durable file");
        check(store.GetSession("missing") is null && !store.DeleteSession("missing"), "SQLite missing reads and deletes are idempotent");
        AgentSession saved;
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), store, Options))
        {
            var first = manager.ProcessMessage(envelope);
            saved = manager.ProcessMessage(envelope with
            {
                Messages = [new("seller", "second persisted message", DateTime.SpecifyKind(Start.UtcDateTime.AddSeconds(1), DateTimeKind.Unspecified))]
            });
            check(saved.Messages.Count == 2 && Equal(store.GetSession(first.SessionId), saved),
                "SQLite saves full session, messages, identity, reception times and state history");
        }
        check(Count(path, "Sessions") == 1 && Count(path, "Messages") == 2 && Count(path, "StateHistory") == 1,
            "SQLite stores exactly the required three relational histories");
        var reopened = new SQLiteAgentSessionStore(path);
        check(Equal(reopened.GetSession(saved.SessionId), saved), "fresh store reads the same persisted snapshot");
        Reject<NotSupportedException>(() => ((IList<AgentSession>)reopened.LoadSessions()).Clear(),
            "loaded session collections are immutable");
        Reject<NotSupportedException>(() => ((IList<AgentSessionMessage>)reopened.GetSession(saved.SessionId)!.Messages).Clear(),
            "loaded message history is immutable");
        Reject<NotSupportedException>(() => ((IList<AgentSessionStateChange>)reopened.GetSession(saved.SessionId)!.StateHistory).Clear(),
            "loaded lifecycle history is immutable");
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), reopened, Options))
        {
            check(Equal(manager.GetSession(saved.SessionId), saved), "fresh manager restores the complete Active snapshot");
            check(Equal(manager.ProcessMessage(envelope), saved), "recovered dedupe index ignores pre-restart generated-ID retry");
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing check executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--agent-session-persistence-child");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(saved.SessionId);
        using (var child = Process.Start(start) ?? throw new InvalidOperationException("Persistence child did not start."))
        {
            try
            {
                if (!child.WaitForExit(15000)) throw new TimeoutException("Persistence child timed out.");
                check(child.ExitCode == 0, "independent process restores session and deduplicates the original message");
            }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        }
        check(new SQLiteAgentSessionStore(path).GetSession(saved.SessionId) is { Messages.Count: 3 } childResult &&
            childResult.Messages[2].Content == "child append", "independent process append survives its exit");

        var kindsPath = Database("kinds");
        var kindsStore = new SQLiteAgentSessionStore(kindsPath);
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), kindsStore, Options))
        {
            var kinds = manager.ProcessMessage(envelope with
            {
                Messages = Enum.GetValues<DateTimeKind>().Select(kind =>
                    new EnvelopeMessage("buyer", "same text", DateTime.SpecifyKind(Start.UtcDateTime, kind))).ToArray()
            });
            check(kinds.Messages.Count == 3 && Equal(new SQLiteAgentSessionStore(kindsPath).GetSession(kinds.SessionId), kinds),
                "SQLite losslessly roundtrips all DateTime kinds with identical ticks");
            manager.ProcessMessage(envelope with { BuyerId = "BUYER" });
            manager.ProcessMessage(envelope with { SellerId = "SELLER" });
            manager.ProcessMessage(envelope with { Source = "OTHER" });
            check(kindsStore.LoadSessions().Count == 4, "SQLite Active uniqueness preserves exact case-sensitive identity isolation");
        }

        var lifecyclePath = Database("lifecycle");
        var lifecycleStore = new SQLiteAgentSessionStore(lifecyclePath);
        var lifecycleClock = new ManualClock(Start);
        AgentSession archived, inactive, active;
        using (var manager = new AgentSessionManager(Timeout, lifecycleClock, lifecycleStore, Options))
        {
            archived = manager.ProcessMessage(Envelope("archived"));
            lifecycleClock.Advance(TimeSpan.FromMinutes(15));
            manager.SweepLifecycle();
            archived = manager.GetSession(archived.SessionId)!;
            inactive = manager.ProcessMessage(Envelope("inactive"));
            lifecycleClock.Advance(Timeout);
            manager.SweepLifecycle();
            inactive = manager.GetSession(inactive.SessionId)!;
            active = manager.ProcessMessage(Envelope("active"), ["native-active"]);
        }
        var restartClock = new ManualClock(lifecycleClock.GetUtcNow());
        using (var manager = new AgentSessionManager(Timeout, restartClock, new SQLiteAgentSessionStore(lifecyclePath), Options))
        {
            check(Equal(manager.GetSession(archived.SessionId), archived), "Archived state and complete transition history restore");
            check(Equal(manager.GetSession(inactive.SessionId), inactive), "Inactive state and last activity restore");
            check(Equal(manager.GetSession(active.SessionId), active), "Active state and last activity restore");
            check(Equal(manager.ProcessMessage(Envelope("active"), ["native-active"]), active),
                "explicit native ID remains idempotent after restart");
            var renewed = manager.ProcessMessage(Envelope("inactive"));
            check(renewed.SessionId != inactive.SessionId && renewed.Messages.Count == 1 &&
                manager.GetSession(inactive.SessionId)!.Messages.Count == 1,
                "restored Inactive history never leaks into a new Active session");
            restartClock.Advance(TimeSpan.FromMinutes(10));
            manager.SweepLifecycle();
            check(manager.GetSession(inactive.SessionId)!.Status == AgentSessionStatus.Archived,
                "restored Inactive retention uses original LastActiveTime rather than restart time");
            check(Equal(manager.GetSession(archived.SessionId), archived), "restored Archived snapshot never changes on sweeps");
        }

        var deadlinePath = Database("deadline");
        AgentSession deadlineSession;
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), new SQLiteAgentSessionStore(deadlinePath), Options))
            deadlineSession = manager.ProcessMessage(envelope);
        var deadlineClock = new ManualClock(Start + Timeout - TimeSpan.FromTicks(1));
        using (var manager = new AgentSessionManager(Timeout, deadlineClock, new SQLiteAgentSessionStore(deadlinePath), Options))
        {
            check(manager.GetSession(deadlineSession.SessionId)!.Status == AgentSessionStatus.Active,
                "downtime just before the idle boundary preserves Active");
            deadlineClock.CorrectClock(TimeSpan.FromDays(1));
            check(manager.SweepLifecycle() == 0, "after recovery forward UTC correction does not change monotonic deadlines");
            deadlineClock.CorrectClock(TimeSpan.FromDays(-2));
            deadlineClock.Advance(TimeSpan.FromTicks(1));
            check(manager.SweepLifecycle() == 1 && manager.GetSession(deadlineSession.SessionId)!.Status == AgentSessionStatus.Inactive,
                "recovered remaining one tick expires despite backward UTC correction");
            check(manager.GetSession(deadlineSession.SessionId)!.LastActiveTime == Start,
                "recovery and lifecycle preserve the original LastActiveTime");
            deadlineClock.Advance(Options.ArchiveAfterIdleTimeout);
            check(manager.SweepLifecycle() == 1, "recovered archive boundary still uses monotonic time");
        }
        foreach (var minutes in new[] { 5, 15 })
        {
            var downtimePath = Database("downtime-" + minutes);
            AgentSession original;
            using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), new SQLiteAgentSessionStore(downtimePath), Options))
                original = manager.ProcessMessage(envelope);
            var downtimeStore = new SQLiteAgentSessionStore(downtimePath);
            using var restored = new AgentSessionManager(Timeout, new ManualClock(Start.AddMinutes(minutes)), downtimeStore, Options);
            var result = restored.GetSession(original.SessionId)!;
            check(result.Status == (minutes == 5 ? AgentSessionStatus.Inactive : AgentSessionStatus.Archived) &&
                result.StateHistory.Count == (minutes == 5 ? 2 : 3), "startup immediately applies idle and archive transitions elapsed during shutdown");
            check(Equal(downtimeStore.GetSession(original.SessionId), result) && result.LastActiveTime == Start,
                "startup lifecycle transitions are persisted without refreshing activity");
        }
        var futurePath = Database("future-clock");
        AgentSession future;
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), new SQLiteAgentSessionStore(futurePath), Options))
            future = manager.ProcessMessage(envelope);
        var futureClock = new ManualClock(Start.AddHours(-1));
        using (var manager = new AgentSessionManager(Timeout, futureClock, new SQLiteAgentSessionStore(futurePath), Options))
        {
            check(manager.GetSession(future.SessionId)!.Status == AgentSessionStatus.Active, "negative wall-clock downtime is clamped to zero");
            futureClock.Advance(Timeout);
            check(manager.SweepLifecycle() == 1, "clamped recovery continues to expire using monotonic duration");
        }

        var rollbackPath = Database("rollback");
        var rollbackStore = new SQLiteAgentSessionStore(rollbackPath);
        var rollbackClock = new ManualClock(Start);
        using (var manager = new AgentSessionManager(Timeout, rollbackClock, rollbackStore, Options with { MaxSessions = 1 }))
        {
            var original = manager.ProcessMessage(envelope, ["original"]);
            Sql(rollbackPath, """
                CREATE TRIGGER FailMessage BEFORE INSERT ON Messages
                WHEN NEW.Content='FAIL' BEGIN SELECT RAISE(ABORT, 'synthetic message failure'); END;
                """);
            Reject<SqliteException>(() => manager.ProcessMessage(envelope with
            {
                Messages = [new("buyer", "successful first row", Start.UtcDateTime), new("buyer", "FAIL", Start.UtcDateTime)]
            }, ["append-1", "append-2"]), "SQLite mid-batch failure reaches caller");
            check(Equal(rollbackStore.GetSession(original.SessionId), original) && Equal(manager.GetSession(original.SessionId), original) &&
                Count(rollbackPath, "Messages") == 1, "failed snapshot rewrite rolls back metadata, deletes and earlier message inserts");
            Sql(rollbackPath, "DROP TRIGGER FailMessage;");
            check(manager.ProcessMessage(envelope with { Messages = [new("buyer", "retry", Start.UtcDateTime)] }, ["append-1"]).Messages.Count == 2,
                "rolled-back message IDs remain available for a successful retry");
            rollbackClock.Advance(Timeout);
            Sql(rollbackPath, """
                CREATE TRIGGER FailState BEFORE INSERT ON StateHistory
                WHEN NEW.NewState=1 BEGIN SELECT RAISE(ABORT, 'synthetic state failure'); END;
                """);
            Reject<SqliteException>(() => manager.SweepLifecycle(), "SQLite lifecycle failure reaches caller");
            check(rollbackStore.GetSession(original.SessionId)!.Status == AgentSessionStatus.Active &&
                Count(rollbackPath, "StateHistory") == 1 && Count(rollbackPath, "Messages") == 2,
                "failed lifecycle write rolls back all three tables");
            Sql(rollbackPath, "DROP TRIGGER FailState;");
            check(manager.SweepLifecycle() == 1, "lifecycle retries after transactional failure");
            Sql(rollbackPath, """
                CREATE TRIGGER FailDelete BEFORE DELETE ON Sessions
                BEGIN SELECT RAISE(ABORT, 'synthetic delete failure'); END;
                """);
            Reject<SqliteException>(() => manager.ProcessMessage(Envelope("other")), "SQLite eviction failure reaches caller");
            check(manager.GetSession(original.SessionId) is { Status: AgentSessionStatus.Inactive } && Count(rollbackPath, "Messages") == 2,
                "failed cascading delete retains manager and database history");
            Sql(rollbackPath, "DROP TRIGGER FailDelete;");
            var replacement = manager.ProcessMessage(Envelope("other"));
            check(rollbackStore.GetSession(original.SessionId) is null && Count(rollbackPath, "Sessions") == 1 &&
                Count(rollbackPath, "Messages") == 1 && Count(rollbackPath, "StateHistory") == 1,
                "successful capacity eviction cascades messages and transition rows");
            Reject<InvalidDataException>(() => rollbackStore.SaveSession(replacement with
            {
                Messages = [replacement.Messages[0], replacement.Messages[0]]
            }), "invalid duplicate snapshot IDs are rejected before database writes");
            check(Equal(rollbackStore.GetSession(replacement.SessionId), replacement), "invalid snapshot leaves SQLite unchanged");
            check(rollbackStore.DeleteSession(replacement.SessionId) && !rollbackStore.DeleteSession(replacement.SessionId) &&
                Count(rollbackPath, "Messages") == 0 && Count(rollbackPath, "StateHistory") == 0, "direct SQLite delete cascades and stays idempotent");
        }

        var limitsPath = Database("limits");
        var limitsStore = new SQLiteAgentSessionStore(limitsPath);
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), limitsStore, Options))
        {
            manager.ProcessMessage(envelope with { Messages = [message, message with { Text = "two" }] });
            manager.ProcessMessage(Envelope("other"));
        }
        Reject<AgentSessionCapacityException>(() => new AgentSessionManager(Timeout, new ManualClock(Start.AddDays(1)),
            limitsStore, Options with { MaxSessions = 1 }), "recovery refuses excess session capacity without deleting stored history");
        Reject<AgentSessionCapacityException>(() => new AgentSessionManager(Timeout, new ManualClock(Start.AddDays(1)),
            limitsStore, Options with { MaxMessagesPerSession = 1 }), "recovery refuses excess message capacity without truncating history");
        check(limitsStore.LoadSessions().All(s => s.Status == AgentSessionStatus.Active) && Count(limitsPath, "Messages") == 3,
            "startup capacity validation runs before any lifecycle writes");

        var evictionPath = Database("recovered-eviction");
        var evictionClock = new ManualClock(Start);
        var evictionStore = new SQLiteAgentSessionStore(evictionPath);
        AgentSession oldest, newer;
        using (var manager = new AgentSessionManager(Timeout, evictionClock, evictionStore, Options with { MaxSessions = 2 }))
        {
            oldest = manager.ProcessMessage(Envelope("oldest"));
            evictionClock.Advance(TimeSpan.FromMinutes(1));
            newer = manager.ProcessMessage(Envelope("newer"));
        }
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start.AddMinutes(7)),
            new SQLiteAgentSessionStore(evictionPath), Options with { MaxSessions = 2 }))
        {
            manager.ProcessMessage(Envelope("new"));
            check(evictionStore.GetSession(oldest.SessionId) is null && evictionStore.GetSession(newer.SessionId) is not null &&
                evictionStore.LoadSessions().Count == 2, "recovered eviction orders original idle ages and remains capacity bounded");
        }

        var memory = new InMemoryAgentSessionStore();
        memory.SaveSession(saved);
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), memory, Options))
            check(Equal(manager.GetSession(saved.SessionId), saved), "enumeration and recovery also work through the InMemory store abstraction");

        var corruptPath = Database("corrupt");
        var corruptStore = new SQLiteAgentSessionStore(corruptPath);
        using (var manager = new AgentSessionManager(Timeout, new ManualClock(Start), corruptStore, Options))
            manager.ProcessMessage(envelope);
        Sql(corruptPath, "DELETE FROM Messages;");
        Reject<InvalidDataException>(() => new AgentSessionManager(Timeout, new ManualClock(Start), corruptStore, Options),
            "incomplete persisted session fails startup instead of silently losing context");
        check(Count(corruptPath, "Sessions") == 1, "corrupt recovery does not delete session evidence");

        var versionPath = Database("version");
        _ = new SQLiteAgentSessionStore(versionPath);
        Sql(versionPath, "PRAGMA user_version=999;");
        Reject<InvalidDataException>(() => new SQLiteAgentSessionStore(versionPath), "unknown schema version refuses open");
        var unrelatedPath = Database("unrelated");
        Sql(unrelatedPath, "CREATE TABLE Other(Value TEXT);");
        Reject<InvalidDataException>(() => new SQLiteAgentSessionStore(unrelatedPath), "unrelated existing database is never adopted or overwritten");
        Reject<ArgumentException>(() => new SQLiteAgentSessionStore(" "), "SQLite rejects a blank path");
        Reject<ArgumentException>(() => new SQLiteAgentSessionStore(":memory:"), "SQLite persistence requires a file-backed path");
        Reject<ArgumentException>(() => store.GetSession(" "), "SQLite rejects a blank lookup ID");
        Reject<ArgumentException>(() => store.DeleteSession(" "), "SQLite rejects a blank delete ID");
        check(new SQLiteAgentSessionStore(path).LoadSessions().All(s =>
            s.Identity.IdentityLevel == IdentityLevel.Observed && s.CurrentProductContext is null && s.IntentHistory.Count == 0),
            "persistence keeps Observed identity and empty business placeholders");
        Console.WriteLine("Persistence databases: " + root);
    }
}
