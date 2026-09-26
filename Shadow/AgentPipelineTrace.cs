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
            "OCR display label; platform identity not inferred"))) return false;
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
            item.Envelope.ProductId, preview.Input.Messages.Count, null, null, "SendStatus=Shadow", preview.AiResponseId));
}
