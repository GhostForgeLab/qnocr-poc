using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Decision;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal sealed class ReplyService(ClientStore store, string? windowScopeId = null, ClientStore? sharedStore = null, IJevGateway? jev = null)
{
    public AssistantReplyService Assistant { get; } = new(store.ReviewRoot);
    public AssistedReplyView SaveDraft(InboxMessage message, string previewId, string text)
    {
        text = ReplyTextPolicy.Normalize(text);
        if (!ReplyTextPolicy.Allowed(text)) throw new InvalidDataException("回复仅允许“烟盒”这一商品名称含“烟”字，其他表述请改成粗支款、细支款、物品或原包装等。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new InvalidDataException("回复需填写 1–4000 字。");
        var current = Latest(message) ?? throw new InvalidDataException("请先生成回复建议。");
        if (current.ReplyPreviewId != previewId) throw new InvalidDataException("回复版本已更新，请重新选择当前消息。");
        if (text == current.EffectiveReply) return current;
        if (current.Status is AssistedReplyStatuses.Approved or AssistedReplyStatuses.Copied)
        {
            // Confirmed text remains immutable. A human edit starts a separately reviewed revision.
            var relative = Path.Combine("reviews", "outbox", previewId);
            if (File.Exists(Path.Combine(store.Root, relative, "assistant-feedback.json")))
                throw new InvalidDataException("这条回复已记录反馈，请重新生成建议后修改。");
            var original = store.ReadValue<ReplyPreview>(Path.Combine(relative, "reply-preview.json"))
                ?? throw new InvalidDataException("原回复记录不存在。");
            if (original.SessionId != message.SessionId || original.Input.MessageId != message.MessageId)
                throw new InvalidDataException("回复不属于当前消息。");
            var id = Guid.NewGuid().ToString("N");
            var revision = original with { ReplyPreviewId = id, CreatedAt = DateTimeOffset.Now };
            var folder = Path.Combine(store.ReviewRoot, "outbox", id);
            Assistant.InitializePreview(folder, revision);
            store.Write(Path.Combine("reviews", "outbox", id, "reply-preview.json"), revision);
            store.Write(Path.Combine("reviews", "outbox", id, "revision-source.json"), new { PreviousPreviewId = previewId });
            var priorAudit = ReadAudit(current);
            if (priorAudit is not null) store.Write(Path.Combine("reviews", "outbox", id, "reply-assessment.json"), priorAudit);
            current = Assistant.Edit(id, Environment.UserName, text);
            var options = OptionIds(message).Select(existing => existing == previewId ? id : existing).ToArray();
            WriteIndex(message, id, options);
            return current;
        }
        return Assistant.Edit(previewId, Environment.UserName, text);
    }
    public AssistedReplyView ConfirmAndCopy(InboxMessage message, string previewId, string text, IAssistantClipboard clipboard)
    {
        var prior = Latest(message);
        if (prior?.ReplyPreviewId == previewId && prior.EditedReply is null && text == prior.EffectiveReply &&
            prior.KnowledgeCategory is "REPLY_UNAVAILABLE" or "JEV_UNAVAILABLE" or "VERIFY_UNAVAILABLE")
            throw new InvalidOperationException("服务故障没有产生有效回复，不能复制错误占位文字。请重试或填写人工回复。");
        var current = SaveDraft(message, previewId, text);
        if (current.Status is not (AssistedReplyStatuses.Approved or AssistedReplyStatuses.Copied))
            current = Assistant.Approve(current.ReplyPreviewId, Environment.UserName, current.ReplySha256);
        var copied = Assistant.Copy(current.ReplyPreviewId, Environment.UserName, current.ReplySha256, clipboard, allowRepeat: true);
        (sharedStore ?? store).Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ReplyCopied,
            new() { WindowId = windowScopeId, SessionId = message.SessionId, MessageId = message.MessageId, PreviewId = copied.ReplyPreviewId, Success = true });
        return copied;
    }
    public async Task<AssistedReplyView> GenerateAsync(InboxMessage message, SessionContextView context,
        CatalogProduct? product, ClientSettings settings, CancellationToken token, Action<JevAssessment>? onAnalyzed = null) =>
        (await GenerateOptionsAsync(message, context, product, settings, token, 1, onAnalyzed))[0];
    public async Task<AssistedReplyView[]> GenerateOptionsAsync(InboxMessage message, SessionContextView context,
        CatalogProduct? product, ClientSettings settings, CancellationToken token, int count = 3, Action<JevAssessment>? onAnalyzed = null)
    {
        if (windowScopeId is not null && message.WindowScopeId != windowScopeId)
            throw new InvalidDataException("消息不属于当前千牛窗口。");
        if (message.Role != "buyer") throw new InvalidDataException("卖家消息只更新上下文，不作为买家问题生成建议。");
        if (message.SellerId != settings.SellerId || message.SessionId != context.SessionId || message.MessageId != context.MessageId)
            throw new InvalidDataException("店铺或会话已切换，请选择当前店铺消息。");
        if (message.Text.Length > 1000) throw new InvalidDataException("当前问题超过 1000 字，请人工处理。");
        if (product is not null && (product.SellerId != message.SellerId || !product.Approved))
            throw new InvalidDataException("请先核对并保存当前店铺商品资料。");
        (sharedStore ?? store).Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ReplyStarted,
            new() { WindowId = windowScopeId, SessionId = message.SessionId, MessageId = message.MessageId, ModelId = settings.AiModel });
        context = context with
        {
            ProductId = product?.ProductId ?? "", CaptureId = message.CaptureId,
            BuyerDisplayName = message.Buyer,
            Identity = new BuyerIdentity(context.BuyerId, "Qianniu.OCR", null, DateTimeOffset.Now,
                context.SessionId, BuyerIdentityStatuses.Observed),
            CurrentProductContext = product is null ? null : new BoundProductContext(context.SessionId, message.SellerId,
                product.ProductId, product.Name, ProductContextSource.Manual, null, DateTimeOffset.Now, "DesktopOperator", message.CaptureId)
        };
        var shop = (sharedStore ?? store).Shop(message.SellerId);
        if (shop?.Approved != true) shop = null;
        var learningService = new LearningService(sharedStore ?? store);
        var learned = product is null ? Array.Empty<LearnedRule>() : learningService.ForReply(product, message.Text);
        var outcomes = message.ContentKind == "Unrecognized"
            ? DesktopReplyPipeline.FallbackOptions(new DesktopReplyOutcome("收到您的消息了，方便再用文字说明一下您想咨询的问题吗？",
                new IntentResult("OTHER", null, false, false, true, "Medium", "VisualClarification"), "VISUAL_CLARIFICATION", true, false), count)
            : await DesktopReplyPipeline.RunOptionsAsync(settings, Connections.Prompt(message, context, product, shop,
                learned),
                product is not null, jev ?? new JevGateway(), token, count, onAnalyzed);
        token.ThrowIfCancellationRequested();
        if (product is not null && !learned.SequenceEqual(learningService.ForReply(product, message.Text)))
            throw new InvalidOperationException("生成期间已确认经验发生变化，本次旧建议已丢弃，请重新生成。");
        var results = new List<AssistedReplyView>();
        foreach (var outcome in outcomes)
        {
            if (outcome.Calls is { } calls)
            {
                calls.ContextMessageCount = context.Messages.Count;
                calls.ContextSellerMessages = context.Messages.Count(m => m.Role == "seller");
            }
            var missing = outcome.Guarded;
            var reply = outcome.Reply; var intent = outcome.Intent;
            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(store.ReviewRoot, "outbox", id);
            var preview = new ReplyPreview(message.SessionId, context.BuyerId, context, reply, null,
                DateTimeOffset.Now, "Shadow", message.CaptureId, Guid.NewGuid().ToString("N"),
                outcome.ModelUsed ? Guid.NewGuid().ToString("N") : "", id, intent,
                missing ? KnowledgeStatuses.Missing : product is null ? KnowledgeStatuses.NotRequired : KnowledgeStatuses.Available,
                product?.Source, outcome.ModelUsed && product is not null, missing, product?.UpdatedAt.ToString("O"), product is null, outcome.Mode);
            Directory.CreateDirectory(folder);
            Assistant.InitializePreview(folder, preview);
            store.Write(Path.Combine("reviews", "outbox", id, "reply-preview.json"), preview);
            store.Write(Path.Combine("reviews", "outbox", id, "reply-assessment.json"), new ReplyAudit
            {
                PipelineVersion = DesktopReplyPipeline.Version, Mode = outcome.Mode, Assessment = outcome.Assessment,
                Verification = outcome.Verification, Failure = outcome.Failure, RejectedDraft = outcome.RejectedDraft, Guarded = outcome.Guarded,
                ModelUsed = outcome.ModelUsed, EvidenceMethod = outcome.EvidenceMethod, Calls = outcome.Calls
            });
            var logger = (sharedStore ?? store).Diagnostics;
            logger.Write(outcome.Mode is "REPLY_UNAVAILABLE" or "JEV_UNAVAILABLE" or "VERIFY_UNAVAILABLE" ? DiagnosticLogLevel.Error :
                outcome.Guarded ? DiagnosticLogLevel.Warning : DiagnosticLogLevel.Information, DiagnosticEvent.ReplyFinished, new()
                {
                    WindowId = windowScopeId, SessionId = message.SessionId, MessageId = message.MessageId, PreviewId = id,
                    ModelId = outcome.Calls?.ActualReplyModel ?? settings.AiModel, Mode = outcome.Mode, Code = outcome.Calls?.ReplyFailureCode ?? outcome.Failure,
                    Intent = intent.IntentName, Purpose = outcome.Assessment?.Purpose?.Name,
                    CoverageProbability = outcome.Assessment?.Coverage.Probability, SupportProbability = outcome.Verification?.Support.Probability,
                    HttpStatus = outcome.Calls?.LastHttpStatus, DurationMs = outcome.Calls?.ElapsedMilliseconds, Count = outcome.Calls?.ReplyRequestsAttempted,
                    ContextMessages = context.Messages.Count, SellerMessages = context.Messages.Count(m => m.Role == "seller"), Success = outcome.ModelUsed
                });
            foreach (var reason in outcome.Calls?.RetryReasons ?? new()) logger.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ReplyRetried,
                new() { WindowId = windowScopeId, PreviewId = id, Code = reason });
                results.Add(Assistant.Show(id)!);
        }
        WriteIndex(message, results[0].ReplyPreviewId, results.Select(r => r.ReplyPreviewId).ToArray());
        return results.ToArray();
    }
    private void WriteIndex(InboxMessage message, string selected, string[] options) =>
        store.Write(Path.Combine("reply-index", message.MessageId.Replace(':', '_') + ".json"), new { PreviewId = selected, PreviewIds = options });
    internal ReplyAudit? ReadAudit(AssistedReplyView view) =>
        store.ReadValue<ReplyAudit>(Path.Combine("reviews", "outbox", view.ReplyPreviewId, "reply-assessment.json"));
    internal bool IsCurrentPipeline(AssistedReplyView view) => ReadAudit(view)?.PipelineVersion == DesktopReplyPipeline.Version;
    private string[] OptionIds(InboxMessage message)
    {
        var path = Path.Combine(store.Root, "reply-index", message.MessageId.Replace(':', '_') + ".json");
        if (!File.Exists(path)) return Array.Empty<string>();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("PreviewIds", out var ids)
            ? ids.EnumerateArray().Select(id => id.GetString()!).ToArray()
            : new[] { json.RootElement.GetProperty("PreviewId").GetString()! };
    }
    public AssistedReplyView[] Options(InboxMessage message)
    {
        if (Latest(message) is null) return Array.Empty<AssistedReplyView>();
        return OptionIds(message).Select(id => Assistant.Show(id) ?? throw new InvalidDataException("回复选项不存在。"))
            .Select(view => view.SessionId == message.SessionId ? view : throw new InvalidDataException("回复选项属于其他会话。")).ToArray();
    }
    public AssistedReplyView SelectOption(InboxMessage message, string previewId)
    {
        var options = Options(message);
        var selected = options.SingleOrDefault(o => o.ReplyPreviewId == previewId) ?? throw new InvalidDataException("回复选项不属于当前消息。");
        WriteIndex(message, previewId, options.Select(o => o.ReplyPreviewId).ToArray()); return selected;
    }
    public AssistedReplyView? Latest(InboxMessage message)
    {
        if (windowScopeId is not null && message.WindowScopeId != windowScopeId)
            throw new InvalidDataException("不能读取其他窗口的回复。");
        var path = Path.Combine(store.Root, "reply-index", message.MessageId.Replace(':', '_') + ".json");
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var view = Assistant.Show(json.RootElement.GetProperty("PreviewId").GetString()!);
        // Message fingerprints can recur in a newly observed session after idle expiration.
        // Keep the old preview as history but never use it as the new session's reply.
        if (view is not null && view.SessionId != message.SessionId) return null;
        return view;
    }
}
