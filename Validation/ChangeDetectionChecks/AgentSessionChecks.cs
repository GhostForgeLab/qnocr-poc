using QnocrPoc.Extension;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class AgentSessionChecks
{
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) { _utcNow += elapsed; _timestamp += elapsed.Ticks; }
        public void CorrectClock(TimeSpan correction) => _utcNow += correction;
    }

    // Integration fixture only: the existing adapter hands off without business services.
    private sealed class SessionExtension(IAgentSessionManager manager) : IAgentExtension
    {
        public AgentSession? Session { get; private set; }
        public void Initialize(AgentExtensionOptions options)
        {
            if (!options.ShadowOnly || options.SendEnabled) throw new InvalidDataException("Unsafe test mode.");
        }
        public Task<AgentExtensionReceipt> ProcessMessageAsync(MessageEnvelope envelope,
            AgentExtensionEvidence evidence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = manager.ProcessMessage(envelope);
            return Task.FromResult(new AgentExtensionReceipt("SessionObserved", evidence.CorrelationId));
        }
        public ReplyPreview? GetReplyPreview(string correlationId) =>
            throw new InvalidOperationException("Session lifecycle must not request a reply preview.");
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, description); return; }
            check(false, description);
        }

        var receivedAt = new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);
        var messageAt = new DateTime(2026, 9, 29, 18, 0, 0, DateTimeKind.Unspecified);
        var clock = new ManualTimeProvider(receivedAt);
        var timeout = TimeSpan.FromMinutes(5);
        IAgentSessionManager manager = new AgentSessionManager(timeout, clock);
        var envelope = new MessageEnvelope("Session.Synthetic", "seller-test", "buyer-test:a", "product-a",
            [new EnvelopeMessage("buyer", "First synthetic message", messageAt)]);

        var first = manager.ProcessMessage(envelope);
        check(Guid.TryParseExact(first.SessionId, "N", out _) && first.Status == AgentSessionStatus.Active &&
            first.Messages.Count == 1 && first.CreatedTime == receivedAt && first.LastActiveTime == receivedAt,
            "first envelope creates an Active Agent session using UTC reception time");
        check(first.Identity == new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId) &&
            first.Identity.IdentityLevel == IdentityLevel.Observed,
            "Agent identity remains Observed without assuming a trusted buyer");
        var firstMessage = first.Messages.Single();
        check(Guid.TryParseExact(firstMessage.MessageId, "N", out _) && firstMessage.SessionId == first.SessionId &&
            firstMessage.Timestamp == messageAt && firstMessage.Timestamp.Kind == DateTimeKind.Unspecified &&
            firstMessage.Content == envelope.Messages[0].Text && firstMessage.Source == envelope.Source &&
            firstMessage.Role == "buyer",
            "Agent message records a local ID, session, source, role, original text and timestamp Kind");

        clock.Advance(TimeSpan.FromMinutes(1));
        var futureMessageAt = messageAt.AddYears(1);
        var second = manager.ProcessMessage(envelope with
        {
            ProductId = "product-b",
            Messages = [new EnvelopeMessage("seller", "Second synthetic message", futureMessageAt)]
        });
        check(second.SessionId == first.SessionId && second.Messages.Count == 2 &&
            second.CreatedTime == first.CreatedTime && second.LastActiveTime == receivedAt.AddMinutes(1) &&
            second.Messages[1].Timestamp == futureMessageAt && second.Messages[1].Role == "seller",
            "same observed identity appends in arrival order and updates activity independently of message time");
        check(second.Messages.Select(message => message.MessageId).Distinct().Count() == 2 &&
            second.Messages.All(message => message.SessionId == first.SessionId),
            "appended messages have distinct local IDs and belong to exactly one session");
        check(first.Messages.Count == 1 && first.LastActiveTime == receivedAt &&
            manager.GetSession(first.SessionId) == second,
            "returned snapshots stay stable while GetSession returns the latest state");
        Reject<NotSupportedException>(() => ((IList<AgentSessionMessage>)second.Messages).Clear(),
            "callers cannot mutate the manager's message history through a snapshot");
        check(second.CurrentProductContext is null && second.IntentHistory.Count == 0 &&
            second.Identity.IdentityLevel == IdentityLevel.Observed,
            "product and intent fields remain reserved and identity is never upgraded");

        clock.Advance(timeout - TimeSpan.FromTicks(1));
        check(manager.ExpireInactiveSessions() == 0 &&
            manager.GetSession(first.SessionId)?.Status == AgentSessionStatus.Active,
            "session remains Active until the full idle timeout since the most recent envelope");
        clock.Advance(TimeSpan.FromTicks(1));
        check(manager.ExpireInactiveSessions() == 1 &&
            manager.GetSession(first.SessionId) is { Status: AgentSessionStatus.Inactive, Messages.Count: 2 } expired &&
            expired.LastActiveTime == second.LastActiveTime,
            "at the idle timeout session becomes Inactive without changing its history or last activity");
        check(manager.ExpireInactiveSessions() == 0, "expiration is idempotent");
        var renewed = manager.ProcessMessage(envelope);
        check(renewed.SessionId != first.SessionId && renewed.Messages.Count == 1 &&
            renewed.CreatedTime == clock.GetUtcNow() && renewed.Status == AgentSessionStatus.Active &&
            manager.GetSession(first.SessionId)?.Messages.Count == 2,
            "message after expiration creates a fresh session and never borrows old history");

        var otherBuyer = manager.ProcessMessage(envelope with { BuyerId = "buyer-test:b" });
        var otherSeller = manager.ProcessMessage(envelope with { SellerId = "seller-other" });
        var otherSource = manager.ProcessMessage(envelope with { Source = "Other.Synthetic" });
        var otherCase = manager.ProcessMessage(envelope with { BuyerId = "BUYER-TEST:A" });
        check(new[] { renewed, otherBuyer, otherSeller, otherSource, otherCase }
            .Select(session => session.SessionId).Distinct().Count() == 5 &&
            new[] { otherBuyer, otherSeller, otherSource, otherCase }.All(session => session.Messages.Count == 1),
            "different buyer, seller, source or identifier casing never merges sessions or histories");
        var delimiterA = manager.ProcessMessage(envelope with { SellerId = "a:b", BuyerId = "c" });
        var delimiterB = manager.ProcessMessage(envelope with { SellerId = "a", BuyerId = "b:c" });
        check(delimiterA.SessionId != delimiterB.SessionId,
            "identity components are structured keys and cannot collide through delimiter concatenation");
        check(manager.GetSession("unknown-local-session") is null, "unknown session ID has no history fallback");

        var batchClock = new ManualTimeProvider(receivedAt);
        var batchManager = new AgentSessionManager(timeout, batchClock);
        var inputMessages = new List<EnvelopeMessage>
        {
            envelope.Messages[0], new("seller", "Synthetic seller record", messageAt.AddSeconds(1))
        };
        var batch = batchManager.ProcessMessage(envelope with { Messages = inputMessages });
        inputMessages.Clear();
        check(batch.Messages.Count == 2 && batch.Messages[0].Role == "buyer" && batch.Messages[1].Role == "seller" &&
            batch.Messages.All(message => message.SessionId == batch.SessionId),
            "multi-message envelope preserves input order and is detached from the caller's collection");
        batchClock.Advance(TimeSpan.FromMinutes(1));
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with
        {
            Messages = [envelope.Messages[0], new EnvelopeMessage("buyer", "", messageAt)]
        }), "invalid batch is rejected before partially appending any message");
        check(batchManager.GetSession(batch.SessionId) == batch,
            "rejected input changes neither existing history nor last activity");
        Reject<ArgumentNullException>(() => batchManager.ProcessMessage(null!), "null envelope is rejected");
        foreach (var missingIdentity in new[]
        {
            envelope with { Source = "" }, envelope with { SellerId = " " }, envelope with { BuyerId = "" }
        })
            Reject<ArgumentException>(() => batchManager.ProcessMessage(missingIdentity),
                "missing identity component is rejected without an unknown-identity session bucket");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with { SchemaVersion = 2 }),
            "unsupported envelope schema is rejected");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with { Messages = [] }),
            "empty envelope cannot create or refresh a session");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with { Messages = null! }),
            "null message collection is rejected");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with { Messages = [null!] }),
            "null message entry is rejected");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with
        {
            Messages = [new EnvelopeMessage("", "Synthetic message", messageAt)]
        }), "message without role is rejected");
        Reject<ArgumentException>(() => batchManager.ProcessMessage(envelope with
        {
            Messages = [new EnvelopeMessage("buyer", "Synthetic message", default)]
        }), "message without timestamp is rejected");
        Reject<ArgumentOutOfRangeException>(() => new AgentSessionManager(TimeSpan.Zero), "zero timeout is rejected");
        Reject<ArgumentOutOfRangeException>(() => new AgentSessionManager(TimeSpan.FromSeconds(-1)),
            "negative timeout is rejected");

        batchClock.Advance(timeout);
        check(batchManager.GetSession(batch.SessionId)?.Status == AgentSessionStatus.Inactive,
            "GetSession applies expiration even without a new message or explicit sweep");
        var lazyClock = new ManualTimeProvider(receivedAt);
        var lazyManager = new AgentSessionManager(timeout, lazyClock);
        var lazyFirst = lazyManager.ProcessMessage(envelope);
        lazyClock.Advance(timeout);
        var lazyNext = lazyManager.ProcessMessage(envelope);
        check(lazyNext.SessionId != lazyFirst.SessionId && lazyNext.Messages.Count == 1 &&
            lazyManager.GetSession(lazyFirst.SessionId)?.Status == AgentSessionStatus.Inactive,
            "ProcessMessage expires an idle session before assigning the next envelope");
        var defaultClock = new ManualTimeProvider(receivedAt);
        var defaultManager = new AgentSessionManager(timeProvider: defaultClock);
        defaultManager.ProcessMessage(envelope);
        defaultClock.Advance(TimeSpan.FromMinutes(30));
        check(defaultManager.ExpireInactiveSessions() == 1, "default idle timeout is 30 minutes");

        var correctionClock = new ManualTimeProvider(receivedAt);
        var correctionManager = new AgentSessionManager(timeout, correctionClock);
        var correctionFirst = correctionManager.ProcessMessage(envelope);
        correctionClock.CorrectClock(TimeSpan.FromDays(1));
        check(correctionManager.ExpireInactiveSessions() == 0,
            "forward UTC clock correction does not prematurely expire a session");
        correctionClock.CorrectClock(TimeSpan.FromDays(-2));
        correctionClock.Advance(TimeSpan.FromMinutes(1));
        var correctionNext = correctionManager.ProcessMessage(envelope);
        check(correctionNext.SessionId == correctionFirst.SessionId &&
            correctionNext.LastActiveTime >= correctionNext.CreatedTime,
            "backward UTC clock correction cannot move LastActiveTime before CreatedTime");
        correctionClock.Advance(timeout);
        check(correctionManager.ExpireInactiveSessions() == 1,
            "monotonic elapsed time expires idle sessions despite backward UTC clock correction");

        var concurrentManager = new AgentSessionManager(timeout, new ManualTimeProvider(receivedAt));
        var sessionIds = new string[32];
        Parallel.For(0, sessionIds.Length, index =>
            sessionIds[index] = concurrentManager.ProcessMessage(envelope).SessionId);
        var concurrent = concurrentManager.GetSession(sessionIds[0])!;
        check(sessionIds.Distinct().Count() == 1 && concurrent.Messages.Count == sessionIds.Length &&
            concurrent.Messages.Select(message => message.MessageId).Distinct().Count() == sessionIds.Length,
            "concurrent same-identity arrivals create one session without losing messages");

        var extension = new SessionExtension(new AgentSessionManager(timeout, new ManualTimeProvider(receivedAt)));
        var adapter = new QnbotAgentAdapter(extension);
        adapter.Initialize();
        var receipt = await adapter.ObserveAsync(new QnbotInboundObservation("seller-test", "buyer-test:a",
            "seller-fixture", "buyer-fixture", "buyer", "Synthetic Adapter message", messageAt,
            "session-test-message", "test-conversation", "test-target", "IsolatedTest", "test-evidence"));
        check(receipt.Status == "SessionObserved" && receipt.ReplyPreviewId is null && !receipt.SendEnabled &&
            receipt.SentMessages == 0 && extension.Session is { Messages.Count: 1 } adapterSession &&
            adapterSession.Messages[0].Content == "Synthetic Adapter message" &&
            adapterSession.Identity.Source == "Qnbot.Native.ReadOnly" &&
            adapterSession.Identity.IdentityLevel == IdentityLevel.Observed,
            "existing Adapter MessageEnvelope reaches Agent context with Observed identity and zero replies or sends");
    }
}
