using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class AgentSessionHardeningChecks
{
    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) { _utcNow += elapsed; _timestamp += elapsed.Ticks; }
        public void CorrectClock(TimeSpan correction) => _utcNow += correction;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ManualTimer(callback, state, dueTime, period);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool IsDisposed { get; private set; }
        // Also permits a late queued callback after Dispose to exercise the manager's guard.
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ProbeStore : IAgentSessionStore
    {
        private readonly InMemoryAgentSessionStore _inner = new();
        public int SaveCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public HashSet<string> SessionIds { get; } = new(StringComparer.Ordinal);
        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }
        public void SaveSession(AgentSession session)
        {
            SaveCalls++;
            if (FailSave) throw new IOException("Synthetic atomic save failure.");
            _inner.SaveSession(session);
            SessionIds.Add(session.SessionId);
        }
        public AgentSession? GetSession(string sessionId) => _inner.GetSession(sessionId);
        public bool DeleteSession(string sessionId)
        {
            DeleteCalls++;
            if (FailDelete) throw new IOException("Synthetic atomic delete failure.");
            SessionIds.Remove(sessionId);
            return _inner.DeleteSession(sessionId);
        }
    }

    public static void Run(Action<bool, string> check)
    {
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, description); return; }
            check(false, description);
        }
        var receivedAt = new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);
        var timeout = TimeSpan.FromMinutes(5);
        var retention = TimeSpan.FromMinutes(10);
        var options = new AgentSessionManagerOptions { ArchiveAfterIdleTimeout = retention };
        var envelope = new MessageEnvelope("Hardening.Synthetic", "seller-test", "buyer-test", "product-test",
            [new EnvelopeMessage("buyer", "Original synthetic content", receivedAt.UtcDateTime)]);
        MessageEnvelope Batch(params string[] contents) => envelope with
        {
            Messages = contents.Select(content => new EnvelopeMessage("buyer", content, receivedAt.UtcDateTime)).ToArray()
        };

        var clock = new ManualTimeProvider(receivedAt);
        var store = new ProbeStore();
        using var manager = new AgentSessionManager(timeout, clock, store, options);
        var first = manager.ProcessMessage(envelope, ["message-1"]);
        check(first.Messages.Single().MessageId == "message-1" && store.GetSession(first.SessionId) is { } saved &&
            saved.SessionId == first.SessionId && saved.Identity == first.Identity && saved.Status == first.Status &&
            saved.CreatedTime == first.CreatedTime && saved.LastActiveTime == first.LastActiveTime &&
            saved.Messages.SequenceEqual(first.Messages) && saved.StateHistory.SequenceEqual(first.StateHistory),
            "stable caller ID and complete initial session are stored");
        clock.Advance(TimeSpan.FromMinutes(1));
        var duplicate = manager.ProcessMessage(envelope, ["message-1"]);
        check(ReferenceEquals(first, duplicate) && duplicate.Messages.Count == 1 && store.SaveCalls == 1,
            "same MessageId retry is ignored without a store write or a new snapshot");
        check(duplicate.LastActiveTime == receivedAt,
            "duplicate-only input does not refresh LastActiveTime");
        var conflicting = manager.ProcessMessage(Batch("Conflicting retry content"), ["message-1"]);
        check(conflicting.Messages.Single().Content == "Original synthetic content",
            "duplicate ID never overwrites the first content");
        var mixed = manager.ProcessMessage(Batch("Conflict", "Second", "Repeated second", "Third"),
            ["message-1", "message-2", "message-2", "message-3"]);
        check(mixed.Messages.Select(message => message.Content).SequenceEqual(["Original synthetic content", "Second", "Third"]),
            "mixed batch keeps first content per ID and appends new IDs in arrival order");
        check(mixed.LastActiveTime == receivedAt.AddMinutes(1) && first.Messages.Count == 1,
            "new IDs refresh activity while prior immutable snapshots stay unchanged");
        var repeatedText = manager.ProcessMessage(Batch("Third"), ["message-4"]);
        check(repeatedText.Messages.Count == 4,
            "equal text with distinct IDs remains distinct legitimate messages");
        var caseId = manager.ProcessMessage(envelope, ["MESSAGE-1"]);
        check(caseId.Messages.Count == 5,
            "message IDs are exact case-sensitive keys");
        var otherIdentity = manager.ProcessMessage(envelope with { BuyerId = "other-buyer" }, ["message-1"]);
        check(otherIdentity.SessionId != first.SessionId && otherIdentity.Messages.Single().MessageId == "message-1",
            "same message ID is independent across observed identities");
        var inBatch = manager.ProcessMessage(Batch("First", "Conflicting", "Last") with { BuyerId = "batch-buyer" },
            ["batch-1", "batch-1", "batch-2"]);
        check(inBatch.Messages.Select(message => message.Content).SequenceEqual(["First", "Last"]),
            "new-session batch also deduplicates before creation");
        clock.Advance(timeout);
        var renewed = manager.ProcessMessage(envelope, ["message-1"]);
        check(renewed.SessionId != first.SessionId && renewed.Messages.Count == 1,
            "dedupe scope ends with a session; a new session can accept the same ID");

        var retryClock = new ManualTimeProvider(receivedAt);
        using var retryManager = new AgentSessionManager(timeout, retryClock);
        var retryFirst = retryManager.ProcessMessage(envelope, ["retry-id"]);
        retryClock.Advance(timeout - TimeSpan.FromTicks(1));
        retryManager.ProcessMessage(envelope, ["retry-id"]);
        retryClock.Advance(TimeSpan.FromTicks(1));
        check(retryManager.ExpireInactiveSessions() == 1 &&
            retryManager.GetSession(retryFirst.SessionId)?.Status == AgentSessionStatus.Inactive,
            "duplicate traffic cannot keep an idle session Active");

        using var concurrentManager = new AgentSessionManager(timeout, new ManualTimeProvider(receivedAt));
        var concurrentIds = new string[64];
        Parallel.For(0, concurrentIds.Length, index =>
            concurrentIds[index] = concurrentManager.ProcessMessage(envelope, ["concurrent-id"]).SessionId);
        check(concurrentIds.Distinct().Count() == 1 && concurrentManager.GetSession(concurrentIds[0])?.Messages.Count == 1,
            "64 concurrent retries create exactly one session and one stored message");

        var invalidClock = new ManualTimeProvider(receivedAt);
        var invalidStore = new ProbeStore();
        using var invalidManager = new AgentSessionManager(timeout, invalidClock, invalidStore);
        var valid = invalidManager.ProcessMessage(envelope, ["valid"]);
        invalidClock.Advance(timeout);
        Reject<ArgumentNullException>(() => invalidManager.ProcessMessage(envelope, null!), "null ID collection is rejected");
        Reject<ArgumentException>(() => invalidManager.ProcessMessage(envelope, []), "ID count mismatch is rejected");
        Reject<ArgumentException>(() => invalidManager.ProcessMessage(envelope, [" "]), "blank ID is rejected");
        Reject<ArgumentException>(() => invalidManager.ProcessMessage(envelope, [null!]), "null ID entry is rejected");
        Reject<ArgumentException>(() => invalidManager.ProcessMessage(Batch("Valid", ""), ["valid", "bad"]),
            "invalid message is rejected even when another ID is a duplicate");
        check(invalidStore.SaveCalls == 1 && invalidStore.GetSession(valid.SessionId)?.Status == AgentSessionStatus.Active,
            "invalid input performs no append, store write or lifecycle mutation");

        var lifecycleClock = new ManualTimeProvider(receivedAt);
        var lifecycleStore = new ProbeStore();
        using var lifecycle = new AgentSessionManager(timeout, lifecycleClock, lifecycleStore, options);
        var initial = lifecycle.ProcessMessage(envelope, ["life-1"]);
        check(initial.StateHistory is [{ From: null, To: AgentSessionStatus.Active, Reason: "Created" }],
            "creation records the Active transition with no previous state");
        lifecycleClock.Advance(timeout - TimeSpan.FromTicks(1));
        check(lifecycle.SweepLifecycle() == 0, "lifecycle stays Active before the exact idle deadline");
        lifecycleClock.Advance(TimeSpan.FromTicks(1));
        check(lifecycle.SweepLifecycle() == 1 && lifecycleStore.GetSession(initial.SessionId)?.Status == AgentSessionStatus.Inactive,
            "idle deadline transitions to Inactive and persists the state");
        lifecycleClock.Advance(retention - TimeSpan.FromTicks(1));
        check(lifecycle.SweepLifecycle() == 0 && lifecycle.GetSession(initial.SessionId)?.Status == AgentSessionStatus.Inactive,
            "Inactive session remains readable until the archive deadline");
        lifecycleClock.Advance(TimeSpan.FromTicks(1));
        check(lifecycle.SweepLifecycle() == 1 && lifecycle.GetSession(initial.SessionId)?.Status == AgentSessionStatus.Archived,
            "exact idle-plus-retention deadline archives the session");
        var archived = lifecycle.GetSession(initial.SessionId)!;
        check(archived.StateHistory.Select(change => change.To).SequenceEqual(
            [AgentSessionStatus.Active, AgentSessionStatus.Inactive, AgentSessionStatus.Archived]) &&
            archived.StateHistory[1].From == AgentSessionStatus.Active &&
            archived.StateHistory[2] is { From: AgentSessionStatus.Inactive, Reason: "RetentionElapsed" } &&
            archived.StateHistory.Select(change => change.ChangedTime).SequenceEqual(
                [receivedAt, receivedAt + timeout, receivedAt + timeout + retention]),
            "state history tracks each transition, time and reason");
        check(archived.LastActiveTime == initial.LastActiveTime && archived.Messages.SequenceEqual(initial.Messages) &&
            initial.Status == AgentSessionStatus.Active && initial.StateHistory.Count == 1,
            "archive preserves messages and last activity without mutating prior snapshots");
        Reject<NotSupportedException>(() => ((IList<AgentSessionStateChange>)archived.StateHistory).Clear(),
            "state history is read-only to callers");
        check(lifecycle.SweepLifecycle() == 0 && lifecycle.ExpireInactiveSessions() == 0,
            "archive and compatibility expiration counts are idempotent");

        var lateClock = new ManualTimeProvider(receivedAt);
        using var lateManager = new AgentSessionManager(timeout, lateClock, options: options);
        var lateFirst = lateManager.ProcessMessage(envelope, ["late"]);
        lateClock.Advance(timeout + retention);
        check(lateManager.SweepLifecycle() == 2 && lateManager.GetSession(lateFirst.SessionId)?.StateHistory.Count == 3,
            "late sweep records both Inactive and Archived without postponing retention");

        var identityClock = new ManualTimeProvider(receivedAt);
        using var identityManager = new AgentSessionManager(timeout, identityClock, options: options);
        var old = identityManager.ProcessMessage(envelope, ["old"]);
        identityClock.Advance(timeout);
        var active = identityManager.ProcessMessage(envelope, ["new"]);
        identityClock.Advance(TimeSpan.FromMinutes(4));
        identityManager.ProcessMessage(envelope, ["keep-1"]);
        identityClock.Advance(TimeSpan.FromMinutes(4));
        identityManager.ProcessMessage(envelope, ["keep-2"]);
        identityClock.Advance(TimeSpan.FromMinutes(2));
        identityManager.SweepLifecycle();
        var activeAfterArchive = identityManager.ProcessMessage(envelope, ["keep-3"]);
        check(identityManager.GetSession(old.SessionId)?.Status == AgentSessionStatus.Archived &&
            activeAfterArchive.SessionId == active.SessionId && activeAfterArchive.Messages.Count == 4,
            "archiving an old session cannot remove the same identity's newer Active mapping");

        var correctionClock = new ManualTimeProvider(receivedAt);
        using var correctionManager = new AgentSessionManager(timeout, correctionClock, options: options);
        var correctionFirst = correctionManager.ProcessMessage(envelope, ["clock"]);
        correctionClock.CorrectClock(TimeSpan.FromDays(2));
        check(correctionManager.SweepLifecycle() == 0, "UTC jump cannot prematurely archive a session");
        correctionClock.CorrectClock(TimeSpan.FromDays(-4));
        correctionClock.Advance(timeout + retention);
        correctionManager.SweepLifecycle();
        var corrected = correctionManager.GetSession(correctionFirst.SessionId)!;
        check(corrected.Status == AgentSessionStatus.Archived && corrected.StateHistory.All(change => change.ChangedTime >= receivedAt),
            "monotonic archive deadlines and nondecreasing transition times survive backward UTC correction");

        var activityClock = new ManualTimeProvider(receivedAt);
        using var activityManager = new AgentSessionManager(timeout, activityClock, options: options);
        activityManager.ProcessMessage(envelope, ["activity-1"]);
        activityClock.CorrectClock(TimeSpan.FromDays(2));
        var futureActivity = activityManager.ProcessMessage(envelope, ["activity-2"]);
        activityClock.CorrectClock(TimeSpan.FromDays(-4));
        activityClock.Advance(timeout + retention);
        activityManager.SweepLifecycle();
        check(activityManager.GetSession(futureActivity.SessionId)!.StateHistory.Skip(1)
            .All(change => change.ChangedTime >= futureActivity.LastActiveTime),
            "transition timestamps cannot precede the last activity after UTC clock correction");

        var capClock = new ManualTimeProvider(receivedAt);
        var capStore = new ProbeStore();
        using var capped = new AgentSessionManager(timeout, capClock, capStore,
            options with { MaxMessagesPerSession = 2, MaxSessions = 2 });
        var capFirst = capped.ProcessMessage(envelope, ["cap-1"]);
        capClock.Advance(TimeSpan.FromMinutes(1));
        Reject<AgentSessionCapacityException>(() => capped.ProcessMessage(Batch("Second", "Third"), ["cap-2", "cap-3"]),
            "batch exceeding message limit is rejected atomically");
        check(capped.GetSession(capFirst.SessionId) == capFirst && capStore.SaveCalls == 1,
            "message capacity rejection preserves history, activity and store contents");
        var capSecond = capped.ProcessMessage(Batch("Conflict", "Second"), ["cap-1", "cap-2"]);
        check(capSecond.Messages.Count == 2, "message capacity counts only distinct new IDs");
        check(capped.ProcessMessage(envelope, ["cap-1"]) == capSecond,
            "duplicate remains harmless at a full message limit");
        Reject<AgentSessionCapacityException>(() => capped.ProcessMessage(envelope, ["cap-3"]),
            "full Active session refuses further unique messages without trimming old IDs");
        var otherActive = capped.ProcessMessage(envelope with { BuyerId = "cap-other" }, ["cap-1"]);
        Reject<AgentSessionCapacityException>(() => capped.ProcessMessage(envelope with { BuyerId = "cap-third" }, ["cap-1"]),
            "session count limit rejects creation when every session is Active");
        check(capStore.SessionIds.Count == 2 && capped.GetSession(capFirst.SessionId) == capSecond &&
            capped.GetSession(otherActive.SessionId) == otherActive && capStore.DeleteCalls == 0,
            "session capacity never deletes an Active session");
        capClock.Advance(TimeSpan.FromMinutes(4));
        capped.ProcessMessage(envelope with { BuyerId = "cap-other" }, ["other-2"]);
        capClock.Advance(TimeSpan.FromMinutes(1));
        var replacement = capped.ProcessMessage(envelope with { BuyerId = "cap-third" }, ["replacement"]);
        check(capped.GetSession(capFirst.SessionId) is null && capStore.GetSession(capFirst.SessionId) is null &&
            capStore.SessionIds.Count == 2 && replacement.Status == AgentSessionStatus.Active &&
            capped.GetSession(otherActive.SessionId)?.Status == AgentSessionStatus.Active,
            "capacity evicts an idle session from manager and store while preserving current Active sessions");

        var evictionClock = new ManualTimeProvider(receivedAt);
        var evictionStore = new ProbeStore();
        using var eviction = new AgentSessionManager(timeout, evictionClock, evictionStore, options with { MaxSessions = 2 });
        var oldest = eviction.ProcessMessage(envelope, ["oldest"]);
        evictionClock.Advance(TimeSpan.FromMinutes(1));
        var younger = eviction.ProcessMessage(envelope with { BuyerId = "younger" }, ["younger"]);
        evictionClock.Advance(timeout + retention);
        eviction.ProcessMessage(envelope with { BuyerId = "newcomer" }, ["newcomer"]);
        check(eviction.GetSession(oldest.SessionId) is null && eviction.GetSession(younger.SessionId)?.Status == AgentSessionStatus.Archived,
            "capacity deterministically evicts the least recently active non-Active session");
        for (var index = 0; index < 20; index++)
        {
            evictionClock.Advance(timeout + retention);
            eviction.ProcessMessage(envelope with { BuyerId = "bounded-" + index }, ["bounded"]);
        }
        check(evictionStore.SessionIds.Count == 2, "repeated idle and archive churn cannot grow retained session count");

        var hugeStore = new ProbeStore();
        using var huge = new AgentSessionManager(timeout, store: hugeStore, options: options with { MaxMessagesPerSession = 1 });
        Reject<AgentSessionCapacityException>(() => huge.ProcessMessage(Batch("One", "Two"), ["one", "two"]),
            "oversized first batch is rejected before creating a session");
        check(hugeStore.SaveCalls == 0 && hugeStore.DeleteCalls == 0, "oversized first batch has no storage side effects");

        using var concurrentCapacity = new AgentSessionManager(timeout, new ManualTimeProvider(receivedAt),
            options: options with { MaxMessagesPerSession = 8, MaxSessions = 1 });
        var accepted = 0;
        var rejected = 0;
        Parallel.For(0, 32, index =>
        {
            try { concurrentCapacity.ProcessMessage(envelope, ["unique-" + index]); Interlocked.Increment(ref accepted); }
            catch (AgentSessionCapacityException) { Interlocked.Increment(ref rejected); }
        });
        check(accepted == 8 && rejected == 24, "concurrent unique messages cannot exceed the per-session count limit");
        using var concurrentSessions = new AgentSessionManager(timeout, new ManualTimeProvider(receivedAt),
            options: options with { MaxSessions = 3 });
        accepted = rejected = 0;
        Parallel.For(0, 16, index =>
        {
            try { concurrentSessions.ProcessMessage(envelope with { BuyerId = "parallel-" + index }, ["id"]); Interlocked.Increment(ref accepted); }
            catch (AgentSessionCapacityException) { Interlocked.Increment(ref rejected); }
        });
        check(accepted == 3 && rejected == 13, "concurrent new identities cannot exceed the global session limit");

        var memoryStore = new InMemoryAgentSessionStore();
        var mutableMessages = first.Messages.ToList();
        var mutableHistory = first.StateHistory.ToList();
        memoryStore.SaveSession(first with { Messages = mutableMessages, StateHistory = mutableHistory });
        mutableMessages.Clear();
        mutableHistory.Clear();
        check(memoryStore.GetSession(first.SessionId) is { Messages.Count: 1, StateHistory.Count: 1 },
            "MemoryStore detaches messages and state history from caller-owned mutable lists");
        memoryStore.SaveSession(mixed);
        check(memoryStore.GetSession(first.SessionId)?.Messages.Count == 3,
            "store upsert saves the full session and messages");
        Reject<NotSupportedException>(() => ((IList<AgentSessionMessage>)memoryStore.GetSession(first.SessionId)!.Messages).Clear(),
            "MemoryStore reads expose immutable message collections");
        check(memoryStore.DeleteSession(first.SessionId) && !memoryStore.DeleteSession(first.SessionId) &&
            memoryStore.GetSession(first.SessionId) is null && memoryStore.GetSession("missing") is null,
            "store delete and missing reads have explicit idempotent behavior");

        var failureClock = new ManualTimeProvider(receivedAt);
        var failureStore = new ProbeStore { FailSave = true };
        using var failureManager = new AgentSessionManager(timeout, failureClock, failureStore, options with { MaxSessions = 1 });
        Reject<IOException>(() => failureManager.ProcessMessage(envelope, ["failure"]), "initial save failure reaches the caller");
        failureStore.FailSave = false;
        var recovered = failureManager.ProcessMessage(envelope, ["failure"]);
        check(recovered.Messages.Count == 1 && failureStore.SessionIds.Count == 1,
            "failed initial save leaves no ghost session or consumed capacity");
        failureClock.Advance(TimeSpan.FromMinutes(1));
        failureStore.FailSave = true;
        Reject<IOException>(() => failureManager.ProcessMessage(Batch("Retry append"), ["append"]), "append save failure reaches the caller");
        failureStore.FailSave = false;
        check(failureManager.GetSession(recovered.SessionId) == recovered,
            "failed append leaves manager history, activity and dedupe IDs unchanged");
        check(failureManager.ProcessMessage(Batch("Retry append"), ["append"]).Messages.Count == 2,
            "retry after failed save can append its previously uncommitted ID");
        failureClock.Advance(timeout);
        failureStore.FailSave = true;
        Reject<IOException>(() => failureManager.SweepLifecycle(), "lifecycle save failure reaches explicit sweep caller");
        check(failureStore.GetSession(recovered.SessionId)?.Status == AgentSessionStatus.Active,
            "failed lifecycle save preserves the prior stored state");
        failureStore.FailSave = false;
        check(failureManager.SweepLifecycle() == 1, "lifecycle transition can recover on a later successful sweep");
        failureStore.FailDelete = true;
        Reject<IOException>(() => failureManager.ProcessMessage(envelope with { BuyerId = "failure-other" }, ["other"]),
            "capacity delete failure rejects creation instead of discarding manager state");
        check(failureManager.GetSession(recovered.SessionId)?.Status == AgentSessionStatus.Inactive && failureStore.SessionIds.Count == 1,
            "failed eviction keeps the retained session readable and capacity bounded");
        failureStore.FailDelete = false;
        check(failureManager.ProcessMessage(envelope with { BuyerId = "failure-other" }, ["other"]).Messages.Count == 1,
            "capacity creation recovers after store deletion succeeds");

        var scheduledClock = new ManualTimeProvider(receivedAt);
        var scheduledStore = new ProbeStore();
        var scheduled = new AgentSessionManager(timeout, scheduledClock, scheduledStore,
            options with { LifecycleSweepInterval = TimeSpan.FromMinutes(1) });
        var scheduledFirst = scheduled.ProcessMessage(envelope, ["scheduled"]);
        check(scheduledClock.Timer is { DueTime: var due, Period: var period } &&
            due == TimeSpan.FromMinutes(1) && period == due,
            "optional scheduler uses an Agent-owned TimeProvider timer with the configured period");
        scheduledClock.Advance(timeout);
        scheduledStore.FailSave = true;
        scheduledClock.Timer!.Fire();
        check(scheduled.LastLifecycleError is IOException && scheduledStore.GetSession(scheduledFirst.SessionId)?.Status == AgentSessionStatus.Active,
            "scheduler records a storage failure without an unhandled callback exception");
        scheduledStore.FailSave = false;
        scheduledClock.Timer.Fire();
        check(scheduled.LastLifecycleError is null && scheduledStore.GetSession(scheduledFirst.SessionId)?.Status == AgentSessionStatus.Inactive,
            "scheduled sweep advances lifecycle without traffic and clears a recovered error");
        scheduledClock.Advance(retention);
        scheduledClock.Timer.Fire();
        check(scheduledStore.GetSession(scheduledFirst.SessionId)?.Status == AgentSessionStatus.Archived,
            "scheduled sweep archives without message or read requests");
        scheduled.Dispose();
        scheduled.Dispose();
        var callsBeforeLateCallback = scheduledStore.SaveCalls;
        scheduledClock.Timer.Fire();
        check(scheduledClock.Timer.IsDisposed && scheduledStore.SaveCalls == callsBeforeLateCallback,
            "Dispose is idempotent, stops its timer and ignores a queued late callback");
        Reject<ObjectDisposedException>(() => scheduled.ProcessMessage(envelope), "disposed manager refuses ingestion");
        Reject<ObjectDisposedException>(() => scheduled.GetSession(scheduledFirst.SessionId), "disposed manager refuses reads");
        Reject<ObjectDisposedException>(() => scheduled.SweepLifecycle(), "disposed manager refuses sweeps");
        Reject<ObjectDisposedException>(() => scheduled.ExpireInactiveSessions(), "disposed manager refuses legacy expiration");
        check(clock.Timer is null, "default construction does not create an unowned background timer");

        foreach (var invalidOptions in new[]
        {
            options with { MaxMessagesPerSession = 0 }, options with { MaxSessions = -1 },
            options with { ArchiveAfterIdleTimeout = TimeSpan.Zero },
            options with { LifecycleSweepInterval = TimeSpan.Zero },
            options with { LifecycleSweepInterval = TimeSpan.FromTicks(1) },
            options with { LifecycleSweepInterval = TimeSpan.FromDays(100) }
        })
            Reject<ArgumentOutOfRangeException>(() => new AgentSessionManager(options: invalidOptions),
                "invalid capacity, retention or timer option is rejected at construction");
        check(new[] { archived, activeAfterArchive, replacement, renewed }.All(session =>
            session.Identity.IdentityLevel == IdentityLevel.Observed && session.CurrentProductContext is null && session.IntentHistory.Count == 0),
            "all hardening paths preserve Observed identity and empty product/intent placeholders");
    }
}
