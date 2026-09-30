using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>In-memory, Agent-owned sessions. Inactive history is retained but never reused.</summary>
public sealed class AgentSessionManager : IAgentSessionManager
{
    private sealed class SessionState(AgentSession snapshot, long lastActiveTimestamp)
    {
        public AgentSession Snapshot { get; set; } = snapshot;
        public long LastActiveTimestamp { get; set; } = lastActiveTimestamp;
    }

    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<SessionIdentity, SessionState> _activeSessions = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AgentSessionManager(TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        if (_idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Idle timeout must be positive.");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AgentSession ProcessMessage(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != 1 || string.IsNullOrWhiteSpace(envelope.Source) ||
            string.IsNullOrWhiteSpace(envelope.SellerId) || string.IsNullOrWhiteSpace(envelope.BuyerId) ||
            envelope.Messages is null)
            throw new ArgumentException("A v1 envelope with an explicit source, seller and buyer is required.", nameof(envelope));

        // Validate the complete copied batch before changing any session, including expiration.
        var messages = envelope.Messages.ToArray();
        if (messages.Length == 0 || messages.Any(message => message is null ||
            string.IsNullOrWhiteSpace(message.Role) || string.IsNullOrWhiteSpace(message.Text) ||
            message.Timestamp == default))
            throw new ArgumentException("At least one complete message is required.", nameof(envelope));

        var identity = new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId);
        lock (_gate)
        {
            var timestamp = _timeProvider.GetTimestamp();
            var now = _timeProvider.GetUtcNow();
            ExpireAt(timestamp);
            if (!_activeSessions.TryGetValue(identity, out var state))
            {
                var session = new AgentSession(Guid.NewGuid().ToString("N"), now, now,
                    Array.AsReadOnly(Array.Empty<AgentSessionMessage>()), identity, AgentSessionStatus.Active);
                state = new SessionState(session, timestamp);
                _activeSessions.Add(identity, state);
                _sessions.Add(session.SessionId, state);
            }

            var appended = messages.Select(message => new AgentSessionMessage(Guid.NewGuid().ToString("N"),
                message.Timestamp, message.Text, envelope.Source, state.Snapshot.SessionId, message.Role));
            state.Snapshot = state.Snapshot with
            {
                // Clock corrections must not move the visible activity time backwards.
                LastActiveTime = now > state.Snapshot.LastActiveTime ? now : state.Snapshot.LastActiveTime,
                Messages = Array.AsReadOnly(state.Snapshot.Messages.Concat(appended).ToArray())
            };
            state.LastActiveTimestamp = timestamp;
            return state.Snapshot;
        }
    }

    public AgentSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            ExpireAt(_timeProvider.GetTimestamp());
            return _sessions.TryGetValue(sessionId, out var state) ? state.Snapshot : null;
        }
    }

    public int ExpireInactiveSessions()
    {
        lock (_gate) return ExpireAt(_timeProvider.GetTimestamp());
    }

    private int ExpireAt(long timestamp)
    {
        var expired = 0;
        foreach (var entry in _activeSessions.ToArray())
        {
            if (_timeProvider.GetElapsedTime(entry.Value.LastActiveTimestamp, timestamp) < _idleTimeout)
                continue;
            entry.Value.Snapshot = entry.Value.Snapshot with { Status = AgentSessionStatus.Inactive };
            _activeSessions.Remove(entry.Key);
            expired++;
        }
        return expired;
    }
}
