using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public interface IAssistantClipboard
{
    void SetText(string text);
}

/// <summary>Clipboard is the only output: this class has no send adapter or Qianniu handle.</summary>
public sealed class WindowsAssistantClipboard : IAssistantClipboard
{
    public void SetText(string text)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { System.Windows.Forms.Clipboard.SetText(text); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Clipboard write failed.", error);
    }
}

/// <summary>Durable, local human-review sidecar. It never invokes a send path.</summary>
public sealed class AssistantReplyService(string stateRoot)
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string AuditPath => Path.Combine(_root, "assistant-actions.jsonl");
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public void InitializePreview(string folder, ReplyPreview preview)
    {
        using var gate = Lock(folder);
        Ensure(folder, preview);
    }

    public IReadOnlyList<AssistedReplyView> List(int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var outbox = Path.Combine(_root, "outbox");
        if (!Directory.Exists(outbox)) return Array.Empty<AssistedReplyView>();
        var entries = new List<AssistedReplyView>();
        foreach (var folder in Directory.EnumerateDirectories(outbox))
        {
            var path = Path.Combine(folder, "reply-preview.json");
            if (!File.Exists(path)) continue;
            try
            {
                var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(path), Json);
                if (preview is null) continue;
                using var gate = Lock(folder);
                entries.Add(View(preview, Ensure(folder, preview)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Console.Error.WriteLine($"Assistant preview held unreadable: {Path.GetFileName(folder)} ({ex.GetType().Name})");
            }
        }
        return entries.OrderByDescending(v => v.CreatedAt).Take(limit).ToArray();
    }
    public AssistedReplyView? Show(string previewId)
    {
        var found = Find(previewId);
        if (found is null) return null;
        var (folder, preview) = found.Value;
        using var gate = Lock(folder);
        return View(preview, Ensure(folder, preview));
    }

    public AssistedReplyView Edit(string previewId, string operatorId, string text)
    {
        ValidateOperator(operatorId);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ArgumentException("Edited reply must contain 1–4000 characters.", nameof(text));
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var state = Ensure(folder, preview);
        if (state.Status is AssistedReplyStatuses.Approved or AssistedReplyStatuses.Copied)
            throw new InvalidOperationException("Approved or copied replies cannot be edited; create a new preview.");
        var now = DateTimeOffset.Now;
        state = state with { EditedReply = text, Status = AssistedReplyStatuses.HumanEdited,
            ReplySha256 = Hash(text), EditedAt = now, EditedBy = operatorId };
        Save(folder, state);
        Append(preview, state, "HUMAN_EDITED", operatorId, "Edited text persisted in assistant state; original AI reply retained.");
        return View(preview, state);
    }

    public AssistedReplyView Approve(string previewId, string operatorId, string expectedHash)
    {
        ValidateOperator(operatorId);
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var state = Ensure(folder, preview);
        if (state.Status is not (AssistedReplyStatuses.AiGenerated or AssistedReplyStatuses.HumanEdited))
            throw new InvalidOperationException("Reply is already approved or copied.");
        VerifyHash(state, expectedHash);
        state = state with { Status = AssistedReplyStatuses.Approved, ApprovedAt = DateTimeOffset.Now, ApprovedBy = operatorId };
        Save(folder, state);
        Append(preview, state, "HUMAN_APPROVED", operatorId, "Exact effective reply SHA-256 reviewed; no send action.");
        return View(preview, state);
    }

    public AssistedReplyView Copy(string previewId, string operatorId, string expectedHash, IAssistantClipboard clipboard)
    {
        ValidateOperator(operatorId);
        ArgumentNullException.ThrowIfNull(clipboard);
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var state = Ensure(folder, preview);
        if (state.Status != AssistedReplyStatuses.Approved)
            throw new InvalidOperationException("Only an approved reply can be copied once.");
        VerifyHash(state, expectedHash);
        try { clipboard.SetText(Effective(state)); }
        catch (Exception ex)
        {
            Append(preview, state, "COPY_FAILED", operatorId, ex.GetType().Name + "; approval retained, no send action.");
            throw;
        }
        state = state with { Status = AssistedReplyStatuses.Copied, CopiedAt = DateTimeOffset.Now, CopiedBy = operatorId };
        Save(folder, state);
        Append(preview, state, "COPY_TO_CLIPBOARD", operatorId, "Approved reply copied; operator must paste and send manually.");
        Append(preview, state, "COPY_ACTION", operatorId, "Clipboard action completed; no send adapter invoked.");
        return View(preview, state);
    }

    /// <summary>Operator self-report only; this method cannot inspect or send a Qianniu message.</summary>
    public AssistedReplyView RecordManualOutcome(string previewId, string operatorId, string outcome)
    {
        ValidateOperator(operatorId);
        if (outcome is not ("PastedAndSentReported" or "NotSentReported"))
            throw new ArgumentException("Use PastedAndSentReported or NotSentReported.", nameof(outcome));
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var state = Ensure(folder, preview);
        if (state.Status != AssistedReplyStatuses.Copied || state.ManualOutcome is not null)
            throw new InvalidOperationException("A single manual outcome may be recorded only after copying.");
        state = state with { ManualOutcome = outcome, OutcomeAt = DateTimeOffset.Now, OutcomeBy = operatorId };
        Save(folder, state);
        Append(preview, state, "MANUAL_OUTCOME_RECORDED", operatorId,
            outcome + "; operator self-report only, not a verified platform receipt.");
        return View(preview, state);
    }
    private AssistedReplyState Ensure(string folder, ReplyPreview preview)
    {
        if (preview.SendStatus != "Shadow" || string.IsNullOrWhiteSpace(preview.Reply))
            throw new InvalidDataException("Only a nonempty Shadow reply can enter assistant review.");
        var path = Path.Combine(folder, "assistant-reply.json");
        if (File.Exists(path))
        {
            var state = JsonSerializer.Deserialize<AssistedReplyState>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Assistant state is unreadable.");
            if (state.ReplyPreviewId != preview.ReplyPreviewId || state.SessionId != preview.SessionId ||
                state.BuyerId != preview.BuyerId || state.OriginalAiReply != preview.Reply ||
                state.ReplySha256 != Hash(Effective(state)))
                throw new InvalidDataException("Assistant state does not match its immutable preview or reply hash.");
            return state;
        }
        var created = new AssistedReplyState(preview.ReplyPreviewId, preview.SessionId, preview.BuyerId,
            preview.Reply, null, AssistedReplyStatuses.AiGenerated, Hash(preview.Reply), preview.CreatedAt);
        Save(folder, created);
        Append(preview, created, "AI_REPLY_CREATED", null, "Human review sidecar initialized; no send action.");
        return created;
    }

    private static AssistedReplyView View(ReplyPreview preview, AssistedReplyState state)
    {
        var identity = preview.Input.Identity;
        var identityStatus = preview.BuyerId.StartsWith("buyer-test", StringComparison.OrdinalIgnoreCase) || identity?.IsTest == true
            ? "Test" : identity?.Status ?? BuyerIdentityStatuses.Unknown;
        var references = preview.Input.ProductReferences ?? Array.Empty<ProductReference>();
        var risk = identityStatus != BuyerIdentityStatuses.Verified ? "High" : references.Count == 0 ? "Medium" : "Low";
        var reason = risk switch { "High" => "Buyer identity is not platform-verified.",
            "Medium" => "No product reference is available.", _ => "Verified identity and a product reference are present; product facts are not verified." };
        return new(preview.ReplyPreviewId, preview.SessionId, preview.BuyerId,
            preview.Input.BuyerDisplayName ?? identity?.BuyerDisplayName, identityStatus, references,
            preview.Input.Messages.LastOrDefault()?.Text ?? "", preview.Reply, state.EditedReply, Effective(state),
            preview.CreatedAt, risk, reason, state.Status, state.ReplySha256,
            state.EditedAt, state.EditedBy, state.ApprovedAt, state.ApprovedBy, state.CopiedAt, state.CopiedBy,
            preview.SendStatus, state.ManualOutcome, state.OutcomeAt, state.OutcomeBy);
    }

    private static string Effective(AssistedReplyState state) => state.EditedReply ?? state.OriginalAiReply;
    private static void VerifyHash(AssistedReplyState state, string expected)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) ||
            !string.Equals(state.ReplySha256, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reviewed reply hash does not match the current text.");
    }
    private static void ValidateOperator(string operatorId)
    {
        if (string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 128)
            throw new ArgumentException("A nonempty operator ID of at most 128 characters is required.", nameof(operatorId));
    }
    private void Save(string folder, AssistedReplyState state) => AtomicJson.Write(Path.Combine(folder, "assistant-reply.json"), state);
    private static FileStream Lock(string folder)
    {
        Directory.CreateDirectory(folder);
        return new FileStream(Path.Combine(folder, "assistant.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Append(ReplyPreview preview, AssistedReplyState state, string kind, string? operatorId, string detail)
    {
        Directory.CreateDirectory(_root);
        var item = new AssistantAuditEvent(DateTimeOffset.Now, kind, preview.ReplyPreviewId,
            preview.CaptureId, preview.SessionId, preview.AiRequestId, preview.AiResponseId,
            operatorId, state.ReplySha256, detail);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(item) + Environment.NewLine);
    }
    private (string Folder, ReplyPreview Preview)? Find(string previewId)
    {
        if (previewId.Length != 32 || !previewId.All(Uri.IsHexDigit))
            throw new ArgumentException("ReplyPreviewId must be 32 hexadecimal characters.", nameof(previewId));
        var outbox = Path.Combine(_root, "outbox");
        if (!Directory.Exists(outbox)) return null;
        (string, ReplyPreview)? found = null;
        foreach (var folder in Directory.EnumerateDirectories(outbox))
        {
            var path = Path.Combine(folder, "reply-preview.json");
            if (!File.Exists(path)) continue;
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(path), Json);
            if (preview?.ReplyPreviewId.Equals(previewId, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (found is not null) throw new InvalidDataException("Duplicate preview ID; assistant action blocked.");
            found = (folder, preview);
        }
        return found;
    }
}
