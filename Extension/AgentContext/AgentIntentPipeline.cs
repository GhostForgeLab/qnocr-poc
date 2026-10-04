using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using QnocrPoc.Adapter;
using QnocrPoc.Decision;
using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Agent-side association; reuses IntentResult without changing messages or persistence schema.</summary>
public sealed record AgentIntentMessageResult(string SessionId, string MessageId, IntentResult? Intent,
    string Provider, DateTimeOffset Timestamp, bool FallbackUsed, string? FallbackReason,
    double LatencyMilliseconds, string? Error = null);

public sealed record AgentIntentBatchResult(AgentSession Session, int AppendedMessages,
    IReadOnlyList<AgentIntentMessageResult> Results);

/// <summary>Exclusive Context owner. Persists first, classifies only appended buyer messages, then stops.</summary>
public sealed class AgentIntentPipeline : IDisposable
{
    private readonly AgentContextPipeline _context;
    private readonly IntentProviderSelection _selection;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, int> _acceptedCounts;
    private readonly int _maxSessions;
    private readonly object _gate = new();
    private IntentProviderTrace? _completion;
    private string? _startupFallbackReason;
    private string? _messageFallbackReason;
    private bool _disposed;

    public AgentIntentPipeline(string databasePath, string intentConfigPath = "Config/intent-provider.json",
        TimeProvider? timeProvider = null, TimeSpan? idleTimeout = null,
        AgentSessionManagerOptions? options = null, HttpClient? jevClient = null, Func<string?>? apiKey = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxSessions = (options ?? new AgentSessionManagerOptions()).MaxSessions;
        _context = new AgentContextPipeline(databasePath, _timeProvider, idleTimeout, options);
        try
        {
            // Persisted history is already accepted, including Context-only history and crash-window appends.
            // Counts consume the Manager's append/dedupe decision; no second MessageId index is built.
            _acceptedCounts = new SQLiteAgentSessionStore(_context.DatabasePath).LoadSessions()
                .ToDictionary(session => session.SessionId, session => session.Messages.Count, StringComparer.Ordinal);
            _selection = IntentClassifierFactory.Create(intentConfigPath, ObserveProvider, jevClient, apiKey);
        }
        catch { _context.Dispose(); throw; }
    }

    public string DatabasePath => _context.DatabasePath;
    public string SelectedProvider => _selection.SelectedProvider;

    private void ObserveProvider(IntentProviderTrace trace)
    {
        if (trace.Event == "JEV_PROVIDER_UNAVAILABLE") _startupFallbackReason = trace.Reason;
        if (trace.Event == "JEV_PROVIDER_FAILED") _messageFallbackReason = trace.Reason;
        if (trace.Event == "INTENT_PROVIDER_RESULT") _completion = trace;
    }

    public AgentIntentBatchResult ProcessMessage(MessageEnvelope envelope)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = _context.ProcessMessage(envelope);
            var previousCount = _acceptedCounts.GetValueOrDefault(session.SessionId);
            var appended = session.Messages.Skip(previousCount).ToArray();
            // Consume the durable append before external calls, even if classification fails.
            _acceptedCounts[session.SessionId] = session.Messages.Count;
            if (_acceptedCounts.Count > _maxSessions)
            {
                var retained = new SQLiteAgentSessionStore(DatabasePath).LoadSessions()
                    .Select(item => item.SessionId).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in _acceptedCounts.Keys.Where(id => !retained.Contains(id)).ToArray())
                    _acceptedCounts.Remove(removed);
            }
            var results = new List<AgentIntentMessageResult>();
            foreach (var message in appended.Where(item => item.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase)))
            {
                _completion = null;
                _messageFallbackReason = null;
                var watch = Stopwatch.StartNew();
                try
                {
                    var input = AgentContextIntentAdapter.Build(session, message.MessageId, _timeProvider.GetUtcNow());
                    IIntentClassifier classifier = _selection.Classifier;
                    var intent = classifier.Classify(input);
                    var trace = _completion;
                    if (trace is null || trace.SessionId != session.SessionId || trace.MessageId != message.MessageId)
                        throw new InvalidDataException("Intent provider trace does not match the accepted message.");
                    results.Add(new AgentIntentMessageResult(session.SessionId, message.MessageId, intent,
                        trace.Provider, trace.Timestamp, trace.FallbackUsed,
                        trace.FallbackUsed ? _messageFallbackReason ?? _startupFallbackReason : null,
                        trace.LatencyMilliseconds ?? watch.Elapsed.TotalMilliseconds));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Context is already durable. Do not introduce another fallback or retry mechanism.
                    results.Add(new AgentIntentMessageResult(session.SessionId, message.MessageId, null,
                        _selection.SelectedProvider, _timeProvider.GetUtcNow(), _selection.FallbackUsed,
                        _messageFallbackReason ?? _startupFallbackReason, watch.Elapsed.TotalMilliseconds,
                        error.GetType().Name));
                }
            }
            return new AgentIntentBatchResult(session, appended.Length, Array.AsReadOnly(results.ToArray()));
        }
    }

    public AgentSession? GetSession(string sessionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _context.GetSession(sessionId);
        }
    }

    public async IAsyncEnumerable<AgentIntentBatchResult> ReadAsync(IMessageSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach (var envelope in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ProcessMessage(envelope);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _selection.Dispose();
            _context.Dispose();
        }
    }
}
