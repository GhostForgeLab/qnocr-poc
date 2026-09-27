using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Feedback;

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
public sealed class AssistantReplyService(string stateRoot, IFeedbackRouter? feedbackRouter = null)
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private readonly IFeedbackRouter _feedbackRouter = feedbackRouter ?? new RuleBasedFeedbackRouter();
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
        HoldRejectedFeedback(folder);
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
        HoldRejectedFeedback(folder);
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
        HoldRejectedFeedback(folder);
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
    public AssistantFeedbackRecord RecordFeedback(string previewId, string operatorId, string feedbackType,
        string? rejectedFinalReply = null, string productAssessment = "Unknown", string contextAssessment = "Unknown",
        string questionCategory = "未分类", string? reason = null, string? batchId = null,
        string? operatorNote = null)
    {
        ValidateOperator(operatorId);
        if (feedbackType is not (ReplyFeedbackTypes.Accepted or ReplyFeedbackTypes.Edited or ReplyFeedbackTypes.Rejected))
            throw new ArgumentException("Feedback must be Accepted, Edited or Rejected.", nameof(feedbackType));
        if (productAssessment is not ("Unknown" or "Correct" or "Incorrect"))
            throw new ArgumentException("Product assessment must be Unknown, Correct or Incorrect.", nameof(productAssessment));
        if (contextAssessment is not ("Unknown" or "Continued" or "Lost"))
            throw new ArgumentException("Context assessment must be Unknown, Continued or Lost.", nameof(contextAssessment));
        if (questionCategory is not ("未分类" or "商品规格" or "材质" or "价格" or "发货" or "售后" or "其他"))
            throw new ArgumentException("Unsupported question category.", nameof(questionCategory));
        if (reason is not null and not ("信息不准确" or "语气问题" or "缺少商品信息" or "其他"))
            throw new ArgumentException("Unsupported feedback reason.", nameof(reason));
        if (batchId is not null && (batchId.Length != 32 || !batchId.All(Uri.IsHexDigit)))
            throw new ArgumentException("BatchId must be 32 hexadecimal characters.", nameof(batchId));
        if (operatorNote is { Length: > 1000 })
            throw new ArgumentException("Operator note may contain at most 1000 characters.", nameof(operatorNote));
        if (rejectedFinalReply is { Length: > 4000 })
            throw new ArgumentException("Final reply may contain at most 4000 characters.", nameof(rejectedFinalReply));
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var state = Ensure(folder, preview);
        var path = Path.Combine(folder, "assistant-feedback.json");
        if (File.Exists(path)) throw new InvalidOperationException("Feedback is already recorded for this preview.");
        if (feedbackType != ReplyFeedbackTypes.Rejected && state.Status != AssistedReplyStatuses.Copied)
            throw new InvalidOperationException("Accepted/Edited feedback requires a copied reply; copy remains a human action.");
        if (feedbackType == ReplyFeedbackTypes.Accepted && state.EditedReply is not null ||
            feedbackType == ReplyFeedbackTypes.Edited && state.EditedReply is null)
            throw new InvalidOperationException("Feedback type must match whether the AI reply was edited.");
        if (feedbackType != ReplyFeedbackTypes.Rejected && rejectedFinalReply is not null)
            throw new ArgumentException("Final text is derived from the copied reply for Accepted/Edited feedback.", nameof(rejectedFinalReply));
        if (string.IsNullOrWhiteSpace(preview.Input.ProductId) && productAssessment != "Unknown")
            throw new InvalidOperationException("Cannot assess an absent current product ID as correct or incorrect.");
        var now = DateTimeOffset.Now;
        var final = feedbackType == ReplyFeedbackTypes.Rejected ? rejectedFinalReply ?? "" : Effective(state);
        var question = preview.Input.Messages.LastOrDefault(m => m.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase))?.Text ?? "";
        var safeQuestion = AssistantSampleRedactor.Redact(question);
        var safeAi = AssistantSampleRedactor.Redact(preview.Reply);
        var safeFinal = AssistantSampleRedactor.Redact(final);
        var safeNote = operatorNote is null ? null : AssistantSampleRedactor.Redact(operatorNote);
        var feedback = new ReplyFeedback(preview.ReplyPreviewId, preview.SessionId, safeAi, safeFinal,
            feedbackType, now, operatorId, safeQuestion, preview.Input.ProductId, preview.Input.Messages.Count,
            productAssessment, contextAssessment, questionCategory, reason, batchId, safeNote,
            preview.Decision?.IntentName);
        var sample = new ConversationSample(preview.ReplyPreviewId, preview.SessionId, preview.CaptureId,
            safeQuestion, safeAi, safeFinal, feedbackType, preview.Input.ProductId,
            (preview.Input.ProductReferences ?? Array.Empty<ProductReference>()).Select(r => r.ProductId).Distinct(StringComparer.Ordinal).ToArray(),
            preview.Input.Messages.Count, preview.Input.CapturedAt, now, questionCategory, reason, batchId, safeNote,
            preview.Decision?.IntentName);
        var classification = ClassifySafe(feedback);
        var record = new AssistantFeedbackRecord(feedback, sample, classification);
        AtomicJson.Write(path, record);
        Append(preview, state, "FEEDBACK_RECORDED", operatorId, feedbackType + "; local redacted sample, no model training or send action.");
        Append(preview, state, "FEEDBACK_CLASSIFIED", operatorId, classification.Reason, classification);
        return record;
    }

    /// <summary>Adds a rule classification to an existing human record without changing its human-authored fields.</summary>
    public FeedbackClassification ClassifyExisting(string previewId)
    {
        var (folder, preview) = Find(previewId) ?? throw new FileNotFoundException("Reply preview not found.");
        using var gate = Lock(folder);
        var path = Path.Combine(folder, "assistant-feedback.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Human feedback not found.", path);
        var record = JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Feedback record is unreadable.");
        if (record.Feedback.ReplyPreviewId != preview.ReplyPreviewId || record.Sample.ReplyPreviewId != preview.ReplyPreviewId)
            throw new InvalidDataException("Feedback and preview identity differ.");
        if (record.Classification is not null) return record.Classification;
        var classified = ClassifySafe(record.Feedback with
        { DecisionIntent = record.Feedback.DecisionIntent ?? preview.Decision?.IntentName });
        AtomicJson.Write(path, record with { Classification = classified });
        var state = Ensure(folder, preview);
        Append(preview, state, "FEEDBACK_CLASSIFIED", "rule-router", classified.Reason, classified);
        return classified;
    }

    private FeedbackClassification ClassifySafe(ReplyFeedback feedback)
    {
        FeedbackClassification classified;
        try { classified = _feedbackRouter.Classify(feedback); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            classified = new FeedbackClassification(feedback.ReplyPreviewId, FeedbackClassificationTypes.Other,
                null, "RouterError_" + ex.GetType().Name, FeedbackTargets.None, DateTimeOffset.Now);
        }
        if (classified.ReplyPreviewId != feedback.ReplyPreviewId ||
            classified.FeedbackType is not (FeedbackClassificationTypes.KnowledgeGap or FeedbackClassificationTypes.StyleIssue
                or FeedbackClassificationTypes.RuleIssue or FeedbackClassificationTypes.IntentIssue or FeedbackClassificationTypes.Other) ||
            classified.SuggestedTarget is not (FeedbackTargets.Knowledge or FeedbackTargets.Prompt or FeedbackTargets.Strategy
                or FeedbackTargets.Decision or FeedbackTargets.None) || string.IsNullOrWhiteSpace(classified.Reason) ||
            classified.Reason.Length > 128 || classified.Confidence is { } confidence &&
                (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            return new FeedbackClassification(feedback.ReplyPreviewId, FeedbackClassificationTypes.Other,
                null, "InvalidRouterOutput", FeedbackTargets.None, DateTimeOffset.Now);
        return classified;
    }
    private static void HoldRejectedFeedback(string folder)
    {
        var path = Path.Combine(folder, "assistant-feedback.json");
        if (!File.Exists(path)) return;
        var record = JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Feedback state is unreadable.");
        if (record.Feedback.FeedbackType == ReplyFeedbackTypes.Rejected)
            throw new InvalidOperationException("Rejected AI suggestion cannot later be edited, approved or copied.");
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
            preview.SendStatus, state.ManualOutcome, state.OutcomeAt, state.OutcomeBy,
            preview.Decision, preview.KnowledgeStatus, preview.KnowledgeSource,
            preview.KnowledgeUsedByAi, preview.KnowledgeGuardApplied,
            preview.ProductKnowledgeVersion, preview.MissingProduct, preview.Input.ProductId);
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
    private void Append(ReplyPreview preview, AssistedReplyState state, string kind, string? operatorId, string detail,
        FeedbackClassification? classification = null)
    {
        Directory.CreateDirectory(_root);
        var item = new AssistantAuditEvent(DateTimeOffset.Now, kind, preview.ReplyPreviewId,
            preview.CaptureId, preview.SessionId, preview.AiRequestId, preview.AiResponseId,
            operatorId, state.ReplySha256, detail, classification?.FeedbackType,
            classification?.SuggestedTarget, classification?.Reason);
        RunLogWriter.Append(_root, Path.GetFileName(AuditPath), item);
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
