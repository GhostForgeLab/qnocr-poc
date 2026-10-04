using System.Net.Http;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Association metadata around the existing KnowledgeRequest/KnowledgeResult; no new knowledge schema.</summary>
public sealed record AgentKnowledgeMessageResult(string SessionId, string MessageId, string SellerId,
    string? ProductId, AgentIntentMessageResult Intent, string Provider, int HitCount,
    KnowledgeRequest? Request, KnowledgeResult? Knowledge, ProductContextResolution ProductContext,
    DateTimeOffset Timestamp, string QueryStatus, bool ProviderCalled, string? Error = null);

public sealed record AgentKnowledgeBatchResult(AgentSession Session, int AppendedMessages,
    IReadOnlyList<AgentIntentMessageResult> IntentResults, IReadOnlyList<AgentKnowledgeMessageResult> Results,
    string ResolverSessionId);

/// <summary>Context → existing Intent → existing product resolvers → published-only Knowledge. Stops before AI.</summary>
public sealed class AgentKnowledgePipeline : IDisposable
{
    public const string KnowledgeUnavailable = "KnowledgeUnavailable";
    private readonly AgentIntentPipeline _intent;
    private readonly AgentProductContextAdapter _products;
    private readonly IKnowledgeProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _knowledgeCalls;
    private bool _disposed;

    public AgentKnowledgePipeline(string databasePath, string productStateRoot,
        string productKnowledgePath, string workflowItemsPath, string intentConfigPath = "Config/intent-provider.json",
        TimeProvider? timeProvider = null, TimeSpan? idleTimeout = null, AgentSessionManagerOptions? options = null,
        HttpClient? jevClient = null, Func<string?>? apiKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowItemsPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Workflow mode is mandatory: never use this source's legacy/unreviewed Fields fallback.
        _provider = new ProductKnowledgeProvider(new ProductKnowledgeRepository(
            new LocalFileKnowledgeSource(productKnowledgePath, workflowItemsPath)));
        _products = new AgentProductContextAdapter(productStateRoot);
        _intent = new AgentIntentPipeline(databasePath, intentConfigPath, _timeProvider,
            idleTimeout, options, jevClient, apiKey);
    }

    public string DatabasePath => _intent.DatabasePath;
    public string SelectedIntentProvider => _intent.SelectedProvider;
    public long KnowledgeCalls => Interlocked.Read(ref _knowledgeCalls);

    /// <summary>Prepare the existing Manual Resolver sidecar for an already accepted, active Agent session.</summary>
    public string PrepareManualContext(string sessionId)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = _intent.GetSession(sessionId) ?? throw new KeyNotFoundException("Agent session not found.");
            return _products.Prepare(session).SessionId;
        }
        finally { _gate.Release(); }
    }

    public AgentSession? GetSession(string sessionId)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _intent.GetSession(sessionId);
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentKnowledgeBatchResult> ProcessMessageAsync(MessageEnvelope envelope,
        OcrResult? productOcr = null, string captureId = "", DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var at = capturedAt ?? _timeProvider.GetUtcNow();
            var currentOcr = productOcr is null ? null : productOcr with { Lines = productOcr.Lines.ToArray() };
            var accepted = _intent.ProcessMessage(envelope);
            var results = new List<AgentKnowledgeMessageResult>();
            // Reuse V1.35.0-A's accepted-only execution. No new MessageId dedupe or historic backfill.
            foreach (var intent in accepted.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await QueryAsync(accepted.Session, intent, currentOcr, captureId, at,
                    cancellationToken).ConfigureAwait(false));
            }
            return new AgentKnowledgeBatchResult(accepted.Session, accepted.AppendedMessages,
                accepted.Results, Array.AsReadOnly(results.ToArray()),
                AgentProductContextAdapter.ResolverSessionId(accepted.Session.SessionId));
        }
        finally { _gate.Release(); }
    }

    private async Task<AgentKnowledgeMessageResult> QueryAsync(AgentSession session,
        AgentIntentMessageResult intent, OcrResult? productOcr, string captureId, DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        KnowledgeRequest? request = null;
        var resolution = new ProductContextResolution(null, ProductContextBindingStatus.Unknown, "Not resolved.");
        var called = false;
        AgentKnowledgeMessageResult Result(string status, KnowledgeResult? knowledge = null, string? error = null) =>
            new(session.SessionId, intent.MessageId, session.Identity.SellerId, request?.ProductReference?.ProductId,
                intent, nameof(ProductKnowledgeProvider), knowledge?.ReturnedFields?.Count ?? 0,
                request, knowledge, resolution, _timeProvider.GetUtcNow(), status, called, error);
        try
        {
            if (intent.SessionId != session.SessionId || intent.Intent is null || intent.Error is not null)
                return Result("IntentUnavailable");
            if (!intent.Intent.NeedKnowledge) return Result(KnowledgeStatuses.NotRequired);
            resolution = _products.Resolve(session, productOcr, capturedAt, captureId);
            var context = AgentContextIntentAdapter.Build(session, intent.MessageId, capturedAt) with
            {
                CapturedAt = capturedAt, CaptureId = captureId,
                ProductId = resolution.Current?.ProductId ?? "",
                CurrentProductContext = resolution.Current, ProductContextBindingStatus = resolution.Status
            };
            request = KnowledgeRequestBuilder.Build(context, intent.Intent, session.Identity.SellerId);
            if (string.IsNullOrWhiteSpace(request.SellerId) || request.MissingProductContext ||
                request.ProductReference is null)
                return Result(KnowledgeStatuses.MissingProductContext);
            if (request.NeedFields.Count == 0) return Result(KnowledgeStatuses.NotRequired);
            called = true;
            Interlocked.Increment(ref _knowledgeCalls);
            var knowledge = await _provider.GetAsync(request, cancellationToken).ConfigureAwait(false);
            return Result(knowledge.Success ? KnowledgeStatuses.Available : KnowledgeStatuses.Missing, knowledge);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Context and Intent are already saved/consumed. No AI, retries, legacy facts or other-product fallback.
            return Result(KnowledgeUnavailable, error: error.GetType().Name);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _intent.Dispose();
        }
        finally { _gate.Release(); }
    }
}
