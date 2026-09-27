using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed class AgentPipelineTrace(string runDirectory)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _sessionSeen = new(StringComparer.Ordinal);
    public int Errors { get; private set; }
    public bool Write(AgentPipelineEvent item)
    {
        lock (_gate)
        {
            try
            {
                RunLogWriter.Append(runDirectory, "agent-pipeline.jsonl", item);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Errors++; Console.WriteLine($"[Agent Shadow] audit unavailable: {item.Event}: {ex.GetType().Name}");
                return false;
            }
        }
    }
    public void IntentProviderObserved(IntentProviderTrace observation) =>
        Write(new AgentPipelineEvent(observation.Timestamp, observation.Event,
            observation.CaptureId ?? "", observation.SessionId, null, observation.MessageId,
            null, null, "", 0, null, null, observation.Reason,
            Intent: observation.Intent, Provider: observation.Provider,
            FallbackUsed: observation.FallbackUsed));
    public bool MessageCaptured(DispatchItem item)
    {
        var captureId = item.CaptureId ?? string.Empty;
        var requestId = item.AiRequestId ?? string.Empty;
        var now = DateTimeOffset.Now;
        if (!Write(new(now, "MESSAGE_CAPTURED", captureId, item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, requestId, null, item.Envelope.ProductId, 1, null, null, null))) return false;
        var created = _sessionSeen.Add(item.Session.SessionId);
        if (created && !Write(new(DateTimeOffset.Now, "SESSION_CREATED", captureId, item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, requestId, null, item.Envelope.ProductId, 1, null, null, "Context view created for this run"))) return false;
        if (!Write(new(DateTimeOffset.Now, "BUYER_BOUND", captureId, item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, requestId, null, item.Envelope.ProductId, 1, null, null,
            "IdentityStatus=" + (item.Session.Identity?.Status ?? BuyerIdentityStatuses.Unknown)
                + "; Source=" + (item.Session.Identity?.Source ?? "Unknown") + "; platform identity not inferred"))) return false;
        if (item.Envelope.ProductId.Length > 0 && !Write(new(DateTimeOffset.Now, "PRODUCT_REFERENCE_BOUND", captureId,
            item.Session.SessionId, item.Session.BuyerId, item.MessageId, requestId, null,
            item.Envelope.ProductId, 1, null, null, "Visible in same capture; message association unverified"))) return false;
        if (!Write(new(DateTimeOffset.Now, "MESSAGE_ATTACHED", captureId, item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, requestId, null, item.Envelope.ProductId, 1, null, null,
            item.Envelope.ProductId.Length == 0 ? "No visible product reference" : item.Envelope.ProductBinding))) return false;
        return Write(new(DateTimeOffset.Now, "SESSION_UPDATED", captureId, item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, requestId, null, item.Envelope.ProductId, 1, null, null, null));
    }
    public bool SessionMissing(string captureId, string productId, int messageCount) =>
        Write(new(DateTimeOffset.Now, "SESSION_MISSING", captureId, null, null, null, null, null,
            productId, messageCount, null, null, "Reliable single buyer header unavailable; no AI request"));
    public bool OutOfScope(DispatchItem item) => Write(new(DateTimeOffset.Now, "SESSION_OUT_OF_SCOPE", item.CaptureId ?? "",
        item.Session.SessionId, item.Session.BuyerId, item.MessageId, item.AiRequestId, null,
        item.Envelope.ProductId, 1, null, null, "Buyer session is outside explicitly selected AI test session"));
    public bool DecisionCreated(DispatchItem item, SessionContextView context, IntentResult decision) =>
        Write(new(DateTimeOffset.Now, "DECISION_CREATED", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null,
            $"Rule={decision.RuleVersion}/{decision.MatchedRule}; NeedProductContext={decision.NeedProductContext}; NeedKnowledge={decision.NeedKnowledge}; NeedHumanReview={decision.NeedHumanReview}",
            Intent: decision.IntentName, Confidence: decision.Confidence, RiskLevel: decision.RiskLevel));
    public bool DecisionError(DispatchItem item, SessionContextView? context, string detail) =>
        Write(new(DateTimeOffset.Now, "DECISION_ERROR", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context?.AiRequestId ?? item.AiRequestId, null,
            item.Envelope.ProductId, context?.Messages.Count ?? 1, null, null, detail));
    public bool KnowledgeRequested(DispatchItem item, SessionContextView context,
        KnowledgeRequest request) =>
        Write(new(DateTimeOffset.Now, "KNOWLEDGE_REQUEST", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, "Read-only provider lookup requested; query text omitted",
            Intent: request.Intent, ProductReference: request.ProductReference?.ProductId,
            SellerId: request.SellerId,
            NeedFields: request.NeedFields));
    public bool KnowledgeResolved(DispatchItem item, SessionContextView context,
        KnowledgeRequest request, KnowledgeResult result)
    {
        if (result.StorageSourceType is { } sourceType &&
            !Write(new(DateTimeOffset.Now, "KNOWLEDGE_SOURCE_READ", item.CaptureId ?? "",
                item.Session.SessionId, item.Session.BuyerId, item.MessageId, context.AiRequestId,
                null, item.Envelope.ProductId, context.Messages.Count, null, null,
                "Storage read completed; fact content omitted", SellerId: request.SellerId,
                ProductReference: request.ProductReference?.ProductId,
                ProductVersion: result.ProductVersion, StorageSourceType: sourceType.ToString())))
            return false;
        return Write(new(DateTimeOffset.Now, "KNOWLEDGE_RESULT", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, "Knowledge content is not added to AI input",
            Intent: request.Intent, Source: result.Source, Success: result.Success,
            ProductReference: request.ProductReference?.ProductId,
            NeedFields: request.NeedFields, MissingFields: result.MissingFields,
            ReturnedFields: result.ReturnedFields?.Keys.ToArray(), SellerId: request.SellerId,
            ProductVersion: result.ProductVersion, MissingProduct: result.MissingProduct,
            KnowledgeCategory: result.KnowledgeCategory, KnowledgeId: result.KnowledgeId,
            KnowledgeSourceType: result.ItemSource?.SourceType.ToString(),
            KnowledgeReference: result.ItemSource?.Reference,
            StorageSourceType: result.StorageSourceType?.ToString()));
    }
    public bool KnowledgeError(DispatchItem item, SessionContextView context,
        KnowledgeRequest request, string errorType) =>
        Write(new(DateTimeOffset.Now, "KNOWLEDGE_ERROR", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, errorType + "; AI continues without knowledge",
            Intent: request.Intent, ProductReference: request.ProductReference?.ProductId,
            SellerId: request.SellerId,
            NeedFields: request.NeedFields));
    public bool KnowledgeContextAttached(DispatchItem item, SessionContextView context,
        KnowledgeRequest request, KnowledgeResult result, string status) =>
        Write(new(DateTimeOffset.Now, "KNOWLEDGE_CONTEXT_ATTACHED", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, "Model question receives bounded test facts/status; no Prompt template change",
            Intent: request.Intent, Source: result.Source, Success: result.Success,
            ProductReference: request.ProductReference?.ProductId,
            NeedFields: request.NeedFields, MissingFields: result.MissingFields,
            ReturnedFields: result.ReturnedFields?.Keys.ToArray(), SellerId: request.SellerId,
            ProductVersion: result.ProductVersion, MissingProduct: result.MissingProduct,
            KnowledgeCategory: result.KnowledgeCategory, KnowledgeId: result.KnowledgeId,
            KnowledgeSourceType: result.ItemSource?.SourceType.ToString(),
            KnowledgeReference: result.ItemSource?.Reference,
            StorageSourceType: result.StorageSourceType?.ToString()));
    public bool KnowledgeGuardApplied(DispatchItem item, SessionContextView context,
        KnowledgeRequest request) =>
        Write(new(DateTimeOffset.Now, "KNOWLEDGE_GUARD_APPLIED", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, "Unsupported fact replaced with human-review fallback",
            Intent: request.Intent, ProductReference: request.ProductReference?.ProductId,
            SellerId: request.SellerId,
            MissingFields: request.NeedFields));
    public bool KnowledgeSkipped(DispatchItem item, SessionContextView context, IntentResult decision) =>
        Write(new(DateTimeOffset.Now, "KNOWLEDGE_SKIPPED", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
            context.Messages.Count, null, null, "NeedKnowledge=false; provider not called",
            Intent: decision.IntentName));
    public bool Request(DispatchItem item, SessionContextView context, int? promptLength, string? scope, DateTimeOffset at) =>
        Write(new(at, "AI_REQUEST", item.CaptureId ?? "", item.Session.SessionId, item.Session.BuyerId,
            item.MessageId, context.AiRequestId, null, item.Envelope.ProductId, context.Messages.Count,
            promptLength, scope, $"ModelInputMessageCount={context.ModelInputMessageCount}"));
    public bool Response(DispatchItem item, SessionContextView context, ShadowAdapterResult result, string? aiResponseId) =>
        Write(new(DateTimeOffset.Now, result.Status == "EmptyReply" ? "EMPTY_REPLY" : result.Status == "Converted" ? "AI_RESPONSE" : "AI_ERROR",
            item.CaptureId ?? "", item.Session.SessionId, item.Session.BuyerId, item.MessageId, context.AiRequestId,
            null, item.Envelope.ProductId, context.Messages.Count, null, null, result.Status == "Converted" ? "Reply generated" : result.Reason ?? result.Status,
            aiResponseId));
    public bool Preview(DispatchItem item, ReplyPreview preview) =>
        Write(new(preview.CreatedAt, "REPLY_PREVIEW_CREATED", item.CaptureId ?? "", item.Session.SessionId,
            item.Session.BuyerId, item.MessageId, preview.AiRequestId, preview.ReplyPreviewId,
            item.Envelope.ProductId, preview.Input.Messages.Count, null, null, "SendStatus=Shadow; KnowledgeStatus=" + (preview.KnowledgeStatus ?? "UnknownLegacy") +
                "; KnowledgeUsedByAi=" + preview.KnowledgeUsedByAi + "; GuardApplied=" + preview.KnowledgeGuardApplied, preview.AiResponseId,
            preview.Decision?.IntentName, preview.Decision?.Confidence, preview.Decision?.RiskLevel));
}
