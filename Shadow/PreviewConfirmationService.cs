using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed record PreviewView(ReplyPreview Preview, ReplyDeliveryState Delivery);
public sealed record ConfirmSendOutcome(string Status, ReplyDeliveryState? Delivery,
    SendAdapterResult? SendResult, string? Detail);

/// <summary>Explicit, durable human gate. Merely displaying a preview cannot call a send adapter.</summary>
public sealed class PreviewConfirmationService(string stateRoot)
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private string AuditPath => Path.Combine(_root, "send-lifecycle.jsonl");

    public static string ReplySha256(string reply) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reply)));

    public PreviewView? Show(string previewId)
    {
        var found = Find(previewId);
        if (found is null)
        {
            Append(new(DateTimeOffset.Now, "PREVIEW_NOT_FOUND", previewId, null, null, null, null,
                null, null, null, null, null, false, "No send adapter invoked."));
            return null;
        }
        var (folder, preview) = found.Value;
        using var gate = Lock(folder);
        return new(preview, EnsureInitialized(folder, preview));
    }

    public async Task<ConfirmSendOutcome> ConfirmSendAsync(string previewId, string operatorId,
        string expectedReplySha256, IReplySendAdapter adapter, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 128)
            throw new ArgumentException("A nonempty operator ID (at most 128 characters) is required.", nameof(operatorId));
        if (expectedReplySha256.Length != 64 || !expectedReplySha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The reviewed reply SHA-256 is required.", nameof(expectedReplySha256));
        var found = Find(previewId);
        if (found is null)
        {
            Append(new(DateTimeOffset.Now, "PREVIEW_NOT_FOUND", previewId, null, null, null, null,
                null, operatorId, expectedReplySha256.ToUpperInvariant(), adapter.Mode, null, false,
                "No send adapter invoked."));
            return new("PREVIEW_NOT_FOUND", null, null, "Unknown preview ID.");
        }
        var (folder, preview) = found.Value;
        using var gate = Lock(folder);
        var state = EnsureInitialized(folder, preview);
        if (preview.SendStatus != "Shadow" || ReplySha256(preview.Reply) != state.ReplySha256)
        {
            Append(From(preview, state, "PREVIEW_TAMPERED", operatorId, "Persisted preview differs from reviewed text; no send attempted."));
            return new("PREVIEW_TAMPERED", state, null, "Preview content changed after creation.");
        }
        if (state.Status != ReplyDeliveryStatuses.WaitingConfirmation)
        {
            Append(From(preview, state, state.Status == ReplyDeliveryStatuses.Cancelled ? "PREVIEW_CANCELLED" : "DUPLICATE_CONFIRM",
                operatorId, "Send request not repeated."));
            return new(state.Status == ReplyDeliveryStatuses.Cancelled ? "PREVIEW_CANCELLED" : "DUPLICATE_CONFIRM",
                state, null, "Preview is not awaiting confirmation.");
        }
        if (!string.Equals(state.ReplySha256, expectedReplySha256, StringComparison.OrdinalIgnoreCase))
        {
            Append(From(preview, state, "CONFIRM_TEXT_MISMATCH", operatorId, "Reviewed reply hash differs; no send attempted."));
            return new("CONFIRM_TEXT_MISMATCH", state, null, "Reply changed or review hash is incorrect.");
        }
        token.ThrowIfCancellationRequested();
        var confirmedAt = DateTimeOffset.Now;
        var hostUser = Environment.UserDomainName + "\\" + Environment.UserName;
        state = state with { Status = ReplyDeliveryStatuses.Confirmed, UpdatedAt = confirmedAt,
            ConfirmedBy = operatorId, ConfirmedAt = confirmedAt, AdapterMode = adapter.Mode, HostUser = hostUser };
        Save(folder, state);
        Append(From(preview, state, "CONFIRMED", operatorId, "Exact reply SHA-256 reviewed."));

        var request = new ConfirmedSendRequest(Guid.NewGuid().ToString("N"), preview.ReplyPreviewId,
            preview.CaptureId, preview.SessionId, preview.BuyerId, preview.AiRequestId, preview.AiResponseId,
            preview.Reply, state.ReplySha256, operatorId, confirmedAt, hostUser);
        AtomicJson.Write(Path.Combine(folder, "send-request.json"), request);
        state = state with { Status = ReplyDeliveryStatuses.Sending, UpdatedAt = DateTimeOffset.Now,
            SendRequestId = request.SendRequestId };
        Save(folder, state);
        Append(From(preview, state, "SEND_REQUEST", operatorId, "Adapter invocation follows this durable record."));

        SendAdapterResult result;
        try { result = await adapter.SendAsync(request, token); }
        catch (Exception ex) { result = new("SendFailed", false, false, ex.GetType().Name + "; outcome requires review", DateTimeOffset.Now); }
        state = state with { Status = result.Acknowledged ? ReplyDeliveryStatuses.Sent : ReplyDeliveryStatuses.Failed,
            UpdatedAt = result.CompletedAt, ResultStatus = result.Status, ActualSent = result.ActualSent };
        AtomicJson.Write(Path.Combine(folder, "send-result.json"), result);
        Save(folder, state);
        Append(From(preview, state, result.Acknowledged ? "SEND_RESULT" : "SEND_FAILED", operatorId, result.Detail));
        return new(state.Status, state, result, result.Detail);
    }

    public ConfirmSendOutcome Cancel(string previewId, string operatorId)
    {
        if (string.IsNullOrWhiteSpace(operatorId)) throw new ArgumentException("Operator ID is required.", nameof(operatorId));
        var found = Find(previewId);
        if (found is null)
        {
            Append(new(DateTimeOffset.Now, "PREVIEW_NOT_FOUND", previewId, null, null, null, null,
                null, operatorId, null, null, null, false, "Cancel did not find a preview."));
            return new("PREVIEW_NOT_FOUND", null, null, null);
        }
        var (folder, preview) = found.Value;
        using var gate = Lock(folder);
        var state = EnsureInitialized(folder, preview);
        if (state.Status != ReplyDeliveryStatuses.WaitingConfirmation)
            return new("CANCEL_REJECTED", state, null, "Only an unconfirmed preview can be cancelled.");
        state = state with { Status = ReplyDeliveryStatuses.Cancelled, UpdatedAt = DateTimeOffset.Now, ConfirmedBy = operatorId };
        Save(folder, state);
        Append(From(preview, state, "CANCELLED", operatorId, "No send adapter invoked."));
        return new(ReplyDeliveryStatuses.Cancelled, state, null, null);
    }

    public void InitializePreview(string folder, ReplyPreview preview)
    {
        using var gate = Lock(folder);
        EnsureInitialized(folder, preview);
    }

    private ReplyDeliveryState EnsureInitialized(string folder, ReplyPreview preview)
    {
        var path = Path.Combine(folder, "reply-preview-state.json");
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<ReplyDeliveryState>(File.ReadAllText(path), _json)
                ?? throw new InvalidDataException("Preview state is unreadable; do not send.");
            if (existing.ReplyPreviewId != preview.ReplyPreviewId) throw new InvalidDataException("Preview identity changed; no send permitted.");
            if (existing.Status != ReplyDeliveryStatuses.Created) return existing;
            var resumed = existing with { Status = ReplyDeliveryStatuses.WaitingConfirmation, UpdatedAt = DateTimeOffset.Now };
            Save(folder, resumed);
            Append(From(preview, resumed, "WAITING_CONFIRMATION", null, "Recovered created preview; no send adapter invoked."));
            return resumed;
        }
        if (preview.SendStatus != "Shadow" || string.IsNullOrWhiteSpace(preview.Reply))
            throw new InvalidDataException("Only a nonempty Shadow preview can await human confirmation.");
        var created = new ReplyDeliveryState(preview.ReplyPreviewId, ReplyDeliveryStatuses.Created,
            DateTimeOffset.Now, ReplySha256(preview.Reply));
        Save(folder, created);
        Append(From(preview, created, "PREVIEW_CREATED", null, "Default send disabled."));
        var waiting = created with { Status = ReplyDeliveryStatuses.WaitingConfirmation, UpdatedAt = DateTimeOffset.Now };
        Save(folder, waiting);
        Append(From(preview, waiting, "WAITING_CONFIRMATION", null, "No send adapter invoked."));
        return waiting;
    }

    private (string Folder, ReplyPreview Preview)? Find(string previewId)
    {
        if (previewId.Length != 32 || !previewId.All(Uri.IsHexDigit))
            throw new ArgumentException("ReplyPreviewId must be 32 hexadecimal characters.", nameof(previewId));
        var outbox = Path.Combine(_root, "outbox");
        if (!Directory.Exists(outbox)) return null;
        (string, ReplyPreview)? match = null;
        foreach (var folder in Directory.EnumerateDirectories(outbox))
        {
            var path = Path.Combine(folder, "reply-preview.json");
            if (!File.Exists(path)) continue;
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(path), _json);
            if (preview?.ReplyPreviewId.Equals(previewId, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (match is not null) throw new InvalidDataException("Duplicate preview ID; no send permitted.");
            match = (folder, preview);
        }
        return match;
    }

    private void Save(string folder, ReplyDeliveryState state) =>
        AtomicJson.Write(Path.Combine(folder, "reply-preview-state.json"), state);

    private static FileStream Lock(string folder)
    {
        Directory.CreateDirectory(folder);
        return new FileStream(Path.Combine(folder, "confirm.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Append(SendAuditEvent item)
    {
        Directory.CreateDirectory(_root);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(item) + Environment.NewLine);
    }

    private static SendAuditEvent From(ReplyPreview preview, ReplyDeliveryState state, string kind,
        string? operatorId, string? detail) =>
        new(DateTimeOffset.Now, kind, preview.ReplyPreviewId, preview.CaptureId, preview.SessionId,
            preview.AiRequestId, preview.AiResponseId, state.SendRequestId, operatorId,
            state.ReplySha256, state.AdapterMode, state.ResultStatus, state.ActualSent, detail,
            state.HostUser);
}
