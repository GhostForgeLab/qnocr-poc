using System.Runtime.CompilerServices;
using QnocrPoc.Adapter;
using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Context-only host boundary. Persistence completes before a snapshot is returned.</summary>
public sealed class AgentContextPipeline : IDisposable
{
    private readonly IAgentSessionManager _manager;
    private readonly IIdentityNormalizer _identityNormalizer;

    public AgentContextPipeline(string databasePath, TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null, AgentSessionManagerOptions? options = null,
        IIdentityNormalizer? identityNormalizer = null)
    {
        _identityNormalizer = identityNormalizer ?? new IdentityNormalizer();
        var store = new SQLiteAgentSessionStore(databasePath);
        DatabasePath = store.DatabasePath;
        _manager = new AgentSessionManager(idleTimeout, timeProvider, store, options);
    }

    public string DatabasePath { get; }
    public AgentSession ProcessMessage(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        // This boundary accepts Observed candidates, not verified/native buyer IDs.
        // Use the same normalized key for message fingerprinting and Session lookup.
        return _manager.ProcessMessage(envelope with
        {
            BuyerId = _identityNormalizer.Normalize(envelope.BuyerId)
        });
    }
    public AgentSession? GetSession(string sessionId) => _manager.GetSession(sessionId);

    public async IAsyncEnumerable<AgentSession> ReadAsync(IMessageSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach (var envelope in source.ReadAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ProcessMessage(envelope);
        }
    }

    public void Dispose() => _manager.Dispose();
}
