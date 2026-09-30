using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Agent-owned bounded sessions; never reuses inactive history or upgrades identity.</summary>
public sealed class AgentSessionManager : IAgentSessionManager
{
    private sealed class SessionEntry(AgentSession snapshot, long lastActiveTimestamp, long sequence)
    {
        public AgentSession Snapshot { get; set; } = snapshot;
        public long LastActiveTimestamp { get; set; } = lastActiveTimestamp;
        public long Sequence { get; } = sequence;
        public HashSet<string> MessageIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly TimeSpan _idleTimeout;
    private readonly AgentSessionManagerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentSessionStore _store;
    private readonly ITimer? _timer;
    private readonly Dictionary<SessionIdentity, SessionEntry> _activeSessions = new();
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _sequence;
    private bool _disposed;
    private Exception? _lastLifecycleError;

    public AgentSessionManager(TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null,
        IAgentSessionStore? store = null, AgentSessionManagerOptions? options = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        if (_idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be positive.");
        _options = options ?? new AgentSessionManagerOptions();
        if (_options.MaxMessagesPerSession <= 0 || _options.MaxSessions <= 0 ||
            _options.ArchiveAfterIdleTimeout <= TimeSpan.Zero ||
            (_options.LifecycleSweepInterval is { } interval &&
             (interval <= TimeSpan.Zero || interval.TotalMilliseconds < 1 ||
              interval.TotalMilliseconds > uint.MaxValue - 1)))
            throw new ArgumentOutOfRangeException(nameof(options), "Limits and durations must be positive; timer interval must be 1..4294967294 ms.");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store ?? new InMemoryAgentSessionStore();
        if (_options.LifecycleSweepInterval is { } sweepInterval)
            _timer = _timeProvider.CreateTimer(_ => ScheduledSweep(), null, sweepInterval, sweepInterval);
    }

    /// <summary>Last scheduler failure, cleared by the next successful scheduled sweep.</summary>
    public Exception? LastLifecycleError { get { lock (_gate) return _lastLifecycleError; } }

    // The v1 envelope has no message IDs. Keep legacy ingestion semantics; retries need stable IDs.
    public AgentSession ProcessMessage(MessageEnvelope envelope) => ProcessMessageCore(envelope, null);

    public AgentSession ProcessMessage(MessageEnvelope envelope, IReadOnlyList<string> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        return ProcessMessageCore(envelope, messageIds);
    }

    private AgentSession ProcessMessageCore(MessageEnvelope envelope, IReadOnlyList<string>? messageIds)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.Source) ||
            string.IsNullOrWhiteSpace(envelope.SellerId) || string.IsNullOrWhiteSpace(envelope.BuyerId) ||
            envelope.Messages is null)
            throw new ArgumentException("A v1 envelope with an explicit source, seller and buyer is required.", nameof(envelope));

        // Copy and validate the whole batch before lifecycle, capacity or store mutations.
        var messages = envelope.Messages.ToArray();
        if (messages.Length == 0 || messages.Any(message => message is null ||
            string.IsNullOrWhiteSpace(message.Role) || string.IsNullOrWhiteSpace(message.Text) ||
            message.Timestamp == default))
            throw new ArgumentException("At least one complete message is required.", nameof(envelope));
        var ids = messageIds?.ToArray() ?? messages.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        if (ids.Length != messages.Length || ids.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("One nonblank ID per message is required.", nameof(messageIds));

        var identity = new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId);
        lock (_gate)
        {
            ThrowIfDisposed();
            var timestamp = _timeProvider.GetTimestamp();
            var now = _timeProvider.GetUtcNow();
            SweepAt(timestamp, now);
            _activeSessions.TryGetValue(identity, out var state);
            var seen = new HashSet<string>(state?.MessageIds ?? [], StringComparer.Ordinal);
            var newIndices = Enumerable.Range(0, ids.Length).Where(index => seen.Add(ids[index])).ToArray();
            if (state is not null && newIndices.Length == 0) return state.Snapshot;
            if (newIndices.Length > _options.MaxMessagesPerSession - (state?.Snapshot.Messages.Count ?? 0))
                throw new AgentSessionCapacityException("Session message limit reached; the entire new-message batch was rejected.");

            if (state is null)
            {
                MakeRoom();
                var session = new AgentSession(Guid.NewGuid().ToString("N"), now, now,
                    Array.AsReadOnly(Array.Empty<AgentSessionMessage>()), identity, AgentSessionStatus.Active)
                {
                    StateHistory = Array.AsReadOnly(new[] { new AgentSessionStateChange(null,
                        AgentSessionStatus.Active, now, "Created") })
                };
                state = new SessionEntry(session, timestamp, ++_sequence);
            }

            var appended = newIndices.Select(index => new AgentSessionMessage(ids[index], messages[index].Timestamp,
                messages[index].Text, envelope.Source, state.Snapshot.SessionId, messages[index].Role));
            var next = state.Snapshot with
            {
                LastActiveTime = now > state.Snapshot.LastActiveTime ? now : state.Snapshot.LastActiveTime,
                Messages = Array.AsReadOnly(state.Snapshot.Messages.Concat(appended).ToArray())
            };
            // Store failures do not commit a new session or a partial append into manager state.
            _store.SaveSession(next);
            state.Snapshot = next;
            state.LastActiveTimestamp = timestamp;
            foreach (var index in newIndices) state.MessageIds.Add(ids[index]);
            _activeSessions[identity] = state;
            _sessions[next.SessionId] = state;
            return next;
        }
    }

    public AgentSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            ThrowIfDisposed();
            SweepAt(_timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
            return _sessions.TryGetValue(sessionId, out var state) ? state.Snapshot : null;
        }
    }

    public int ExpireInactiveSessions()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var before = _activeSessions.Count;
            SweepAt(_timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
            return before - _activeSessions.Count;
        }
    }

    public int SweepLifecycle()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return SweepAt(_timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
        }
    }

    private int SweepAt(long timestamp, DateTimeOffset now)
    {
        var transitions = 0;
        foreach (var state in _sessions.Values)
        {
            if (state.Snapshot.Status == AgentSessionStatus.Archived) continue;
            var elapsed = _timeProvider.GetElapsedTime(state.LastActiveTimestamp, timestamp);
            if (elapsed < _idleTimeout) continue;
            var next = state.Snapshot;
            if (next.Status == AgentSessionStatus.Active)
                next = Transition(next, AgentSessionStatus.Inactive, now, "IdleTimeout");
            if (elapsed - _idleTimeout >= _options.ArchiveAfterIdleTimeout)
                next = Transition(next, AgentSessionStatus.Archived, now, "RetentionElapsed");
            if (next == state.Snapshot) continue;
            _store.SaveSession(next);
            transitions += next.StateHistory.Count - state.Snapshot.StateHistory.Count;
            if (state.Snapshot.Status == AgentSessionStatus.Active) _activeSessions.Remove(next.Identity);
            state.Snapshot = next;
        }
        return transitions;
    }

    private static AgentSession Transition(AgentSession session, AgentSessionStatus status,
        DateTimeOffset now, string reason)
    {
        var previousTime = session.StateHistory[^1].ChangedTime;
        if (session.LastActiveTime > previousTime) previousTime = session.LastActiveTime;
        var change = new AgentSessionStateChange(session.Status, status,
            now > previousTime ? now : previousTime, reason);
        return session with { Status = status,
            StateHistory = Array.AsReadOnly(session.StateHistory.Append(change).ToArray()) };
    }

    private void MakeRoom()
    {
        if (_sessions.Count < _options.MaxSessions) return;
        var victim = _sessions.Values.Where(state => state.Snapshot.Status != AgentSessionStatus.Active)
            .OrderBy(state => state.LastActiveTimestamp).ThenBy(state => state.Sequence).FirstOrDefault();
        if (victim is null)
            throw new AgentSessionCapacityException("Session limit reached; all retained sessions are Active.");
        _store.DeleteSession(victim.Snapshot.SessionId);
        _sessions.Remove(victim.Snapshot.SessionId);
    }

    private void ScheduledSweep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                SweepAt(_timeProvider.GetTimestamp(), _timeProvider.GetUtcNow());
                _lastLifecycleError = null;
            }
            catch (Exception error) { _lastLifecycleError = error; }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer?.Dispose();
    }
}
