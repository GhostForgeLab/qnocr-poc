using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Extension.AgentContext;

public sealed record AgentReplyMessageResult(AgentKnowledgeMessageResult Knowledge, ReplyPreview? Preview,
    string Status, string AiProvider, string? AiModel, bool AiInvoked, int AiCalls, string? Error = null);
public sealed record AgentReplyBatchResult(AgentKnowledgeBatchResult Knowledge,
    IReadOnlyList<AgentReplyMessageResult> Results);

/// <summary>Exclusive Agent owner; produces existing Shadow Preview/review files. No delivery dependencies.</summary>
public sealed class AgentReplyPipeline : IDisposable
{
    private readonly AgentKnowledgePipeline _knowledge;
    private readonly string _state;
    private readonly TimeProvider _clock;
    private readonly Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>> _generate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _aiCalls, _previewCount;
    private bool _disposed;

    public AgentReplyPipeline(string databasePath, string productStateRoot, string productKnowledgePath,
        string workflowItemsPath, string replyStateRoot, string aiProviderConfigPath = "Config/ai-provider.json",
        string intentConfigPath = "Config/intent-provider.json", TimeProvider? timeProvider = null,
        HttpClient? jevClient = null, Func<string?>? apiKey = null,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>>? generate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyStateRoot);
        _state = Path.GetFullPath(replyStateRoot);
        _clock = timeProvider ?? TimeProvider.System;
        _generate = generate ?? new ShadowAdapterClient(aiShadow: true,
            aiProviderConfigPath: aiProviderConfigPath, agentReplyPreview: true).ConvertAsync;
        _knowledge = new AgentKnowledgePipeline(databasePath, productStateRoot, productKnowledgePath,
            workflowItemsPath, intentConfigPath, _clock, jevClient: jevClient, apiKey: apiKey);
    }

    public string DatabasePath => _knowledge.DatabasePath;
    public long AiCalls => Interlocked.Read(ref _aiCalls);
    public long PreviewCount => Interlocked.Read(ref _previewCount);
    public long KnowledgeCalls => _knowledge.KnowledgeCalls;
    public string SelectedIntentProvider => _knowledge.SelectedIntentProvider;
    public string PrepareManualContext(string sessionId) => _knowledge.PrepareManualContext(sessionId);
    public AgentSession? GetSession(string sessionId) => _knowledge.GetSession(sessionId);

    public async Task<AgentReplyBatchResult> ProcessMessageAsync(MessageEnvelope envelope,
        OcrResult? productOcr = null, string captureId = "", DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var knowledge = await _knowledge.ProcessMessageAsync(envelope, productOcr, captureId,
                capturedAt, cancellationToken).ConfigureAwait(false);
            var replies = new List<AgentReplyMessageResult>();
            foreach (var result in knowledge.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                replies.Add(await PreviewAsync(knowledge.Session, result, cancellationToken).ConfigureAwait(false));
            }
            return new(knowledge, Array.AsReadOnly(replies.ToArray()));
        }
        finally { _gate.Release(); }
    }

    private async Task<AgentReplyMessageResult> PreviewAsync(AgentSession session,
        AgentKnowledgeMessageResult knowledge, CancellationToken token)
    {
        ShadowAdapterResult? ai = null;
        try
        {
            var requestId = MessageFingerprint.AiRequestId(session.SessionId + ":" + knowledge.MessageId);
            var context = AgentReplyInputAdapter.Build(session, knowledge, requestId);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                session.SessionId + ":" + knowledge.MessageId)));
            var folder = Path.Combine(_state, "outbox", key); // Existing review layout; opaque Agent IDs are not paths.
            var final = Path.Combine(folder, "reply-preview.json");
            if (File.Exists(final)) throw new InvalidDataException("An existing Preview cannot be replaced.");
            string reply;
            string status;
            var guarded = knowledge.QueryStatus is KnowledgeStatuses.Missing or KnowledgeStatuses.MissingProductContext
                or AgentKnowledgePipeline.KnowledgeUnavailable;
            if (guarded)
            {
                reply = KnowledgeReplyGuard.HumanCheckReply;
                status = knowledge.QueryStatus;
            }
            else
            {
                var input = Path.Combine(folder, "model-context.json");
                AgentReplyInputAdapter.Write(input, context, knowledge);
                ai = await _generate(input, null, Path.Combine(folder, "provider"), token).ConfigureAwait(false);
                Interlocked.Add(ref _aiCalls, ai.AiCalls);
                if (ai.Status != "Converted" || !ai.AiInvoked || ai.AiCalls != 1 ||
                    string.IsNullOrWhiteSpace(ai.ReplySuggestion) || ai.ReplySuggestion.Length > 4000 ||
                    ai.ReplySuggestion.StartsWith("错误：", StringComparison.Ordinal))
                    return new(knowledge, null, "AIUnavailable", ai.AiProvider ?? "Unknown", ai.AiModel,
                        ai.AiInvoked, ai.AiCalls, ai.Status);
                if (ai.ReceiptPath is null) throw new InvalidDataException("Provider Preview receipt is missing.");
                using var receipt = JsonDocument.Parse(File.ReadAllText(ai.ReceiptPath));
                var metadata = receipt.RootElement;
                if (metadata.GetProperty("SessionId").GetString() != session.SessionId ||
                    metadata.GetProperty("MessageId").GetString() != knowledge.MessageId ||
                    metadata.GetProperty("AiRequestId").GetString() != requestId ||
                    metadata.GetProperty("SellerId").GetString() != knowledge.SellerId ||
                    metadata.GetProperty("ProductId").GetString() != context.ProductId ||
                    metadata.GetProperty("Mode").GetString() != "AgentReplyPreviewOnly" ||
                    metadata.GetProperty("SendEnabled").GetBoolean() ||
                    metadata.GetProperty("SendEntryInvoked").GetBoolean() ||
                    metadata.GetProperty("SentMessages").GetInt32() != 0 ||
                    metadata.GetProperty("CoordinatorCreated").GetBoolean() ||
                    metadata.GetProperty("ForbiddenBusinessCalls").GetInt32() != 0 ||
                    metadata.GetProperty("ProviderName").GetString() != ai.AiProvider ||
                    metadata.GetProperty("ProviderModel").GetString() != ai.AiModel)
                    throw new InvalidDataException("AI receipt belongs to a different Agent request.");
                reply = ai.ReplySuggestion;
                status = "ReadyForReview";
            }
            var preview = new ReplyPreview(session.SessionId, session.Identity.BuyerId, context, reply,
                null, _clock.GetUtcNow(), "Shadow", context.CaptureId, requestId,
                guarded ? "" : Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                knowledge.Intent.Intent, knowledge.QueryStatus, knowledge.Knowledge?.Source,
                !guarded && knowledge.QueryStatus == KnowledgeStatuses.Available, guarded,
                knowledge.Knowledge?.ProductVersion, knowledge.ProductId is null,
                knowledge.Knowledge?.KnowledgeCategory);
            var pending = Path.Combine(folder, "reply-preview.pending.json");
            AtomicJson.Write(pending, preview);
            new AssistantReplyService(_state).InitializePreview(folder, preview);
            var result = new AgentReplyMessageResult(knowledge, preview, status,
                guarded ? "SafetyGuard" : ai!.AiProvider ?? "Unknown", ai?.AiModel,
                ai?.AiInvoked ?? false, ai?.AiCalls ?? 0);
            AtomicJson.Write(Path.Combine(folder, "agent-reply-result.json"), result);
            File.Move(pending, final); // Publish only after review state and association receipt are valid.
            Interlocked.Increment(ref _previewCount);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(knowledge, null, "AIUnavailable", ai?.AiProvider ?? "Unknown", ai?.AiModel,
                ai?.AiInvoked ?? false, ai?.AiCalls ?? 0, error.GetType().Name);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _knowledge.Dispose(); }
        finally { _gate.Release(); }
    }
}
