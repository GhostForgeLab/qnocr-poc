using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QnocrPoc.Models;

namespace QNAgent;

internal sealed partial class ReceptionSession
{
    private readonly CheckBox _automatic = new() { Content = "买家新消息时自动生成建议", Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _callStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
    private readonly HashSet<string> _autoAttempted = new();
    private readonly HashSet<string> _autoEligible = new();
    private bool _conversationPending;
    private long _dialogueRevision;
    private bool SellerHasReplied(InboxMessage message) => _messages.Any(m => _focus.Contains(m) && m.Role == "seller" &&
        (m.Timestamp > message.Timestamp || m.Timestamp == message.Timestamp && m.Sequence > message.Sequence));
    private CancellationTokenSource? _autoDelay, _generationCancellation;
    private Task? _generationTask;
    private string? _scheduledKey, _generationKey;
    private bool _refreshingSelection;
    internal async Task RecognizeProductWithRetryAsync(Func<InboxMessage, CancellationToken, Task<string?>>? recognize = null)
    {
        var original = _selected ?? throw new InvalidOperationException("请等待当前买家识别完成。");
        recognize ??= _connection.RecognizeCurrentProductAsync;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_stopping || _selected?.MessageId != original.MessageId || !_focus.Contains(original)) return;
            try
            {
                SetStatus("正在识别当前商品…");
                var id = await recognize(_selected, _lifetime.Token);
                if (_stopping || _selected?.MessageId != original.MessageId || !_focus.Contains(original)) return;
                var product = (_replyProduct.ItemsSource as CatalogProduct[])?.SingleOrDefault(p => p.SellerId == original.SellerId && p.ProductId == id);
                if (product is not null) _replyProduct.SelectedItem = product;
                SetStatus(product is not null ? "商品已识别；买家新消息会自动生成建议，也可手工生成当前问题。" : "未识别到已录入商品，可继续使用已选商品或手动选择。");
                QueueAutoGeneration(); return;
            }
            catch (ConversationChangedException)
            {
                SetStatus(attempt == 0 ? "画面发生变化，正在等待稳定后重试识别…" : "画面仍在变化，请稍后识别；已选商品仍可继续使用。");
                if (attempt == 1) { QueueAutoGeneration(); return; }
                await Task.Delay(400, _lifetime.Token);
                for (var wait = 0; wait < 30 && !_focus.Contains(original); wait++)
                {
                    if (_focus.Buyer is not null || _stopping) return; // Never retry against a different buyer.
                    await Task.Delay(100, _lifetime.Token);
                }
            }
        }
    }
    private static bool IsServiceFailure(AssistedReplyView view) => view.KnowledgeCategory is "REPLY_UNAVAILABLE" or "JEV_UNAVAILABLE" or "VERIFY_UNAVAILABLE";
    private void ShowCallStatus(AssistedReplyView view)
    {
        var audit = _replies.ReadAudit(view); var calls = audit?.Calls;
        if (calls is null)
        {
            _callStatus.Text = $"历史回复 {view.CreatedAt:HH:mm} · 本次未重新调用";
            _callStatus.ToolTip = $"旧记录：{view.Decision?.RuleVersion ?? "未记录"}；回复模型{(audit?.ModelUsed == true ? "曾调用" : "未调用或无记录")}。点击重新生成可使用最新流程。";
            return;
        }
        var jev = calls.JevAnalysisSucceeded ? "JEV 已调用" : calls.JevAnalysisAttempted ? "JEV 请求未完成" : "JEV 未调用";
        var ai = calls.ReplyRequestsSucceeded > 0 ? "回复模型已调用" : calls.ReplyRequestsAttempted > 0 ? "回复模型请求失败" : "回复模型未调用";
        _callStatus.Text = $"{jev} · {ai}";
        _callStatus.ToolTip = $"生成时间：{calls.StartedAt:HH:mm:ss}\nJEV：{calls.JevModel}\n意图：{view.DecisionIntent}\n初判：{audit?.Assessment?.Coverage.Choice} / {audit?.Assessment?.Coverage.Probability:P0}\n回复模型：{calls.ActualReplyModel ?? calls.ReplyModel}\n请求 / 成功：{calls.ReplyRequestsAttempted} / {calls.ReplyRequestsSucceeded}\nHTTP：{calls.LastHttpStatus}\n结束原因：{calls.LastFinishReason}\n错误：{calls.ReplyFailureCode} {calls.ReplyFailureMessage}\n重试原因：{string.Join(", ", calls.RetryReasons)}\n核验：{audit?.Verification?.Support.Choice ?? "未执行"}\n依据方式：{audit?.EvidenceMethod ?? "模型回复"}\n上下文：{calls.ContextMessageCount} 条（卖家 {calls.ContextSellerMessages} 条）\n总用时：{calls.ElapsedMilliseconds / 1000.0:F1} 秒";
    }
    private void ProductSelectionChanged()
    {
        if (_refreshingSelection || _selected is null) return;
        CancelGeneration(); _focus.ClearDraft(_selected); _view = null; _reply.Clear(); _callStatus.Text = "";
        ResetIntentCard("商品已切换，等待重新分析");
        _replyState.Text = "商品已切换，等待生成新建议";
        if (_replyProduct.SelectedItem is CatalogProduct product) _sessionProducts[_selected.SessionId] = product.ProductId;
        QueueAutoGeneration();
    }
    private string WorkKey(InboxMessage message, CatalogProduct? product) =>
        $"{message.SessionId}|{message.MessageId}|{message.ContentKind}|{product?.ProductId}|{product?.UpdatedAt:O}|{_shared.Shop(SellerId)?.UpdatedAt:O}";
    private void CancelAutoDelay()
    {
        var source = _autoDelay; _autoDelay = null; _scheduledKey = null;
        CancelSafely(source);
    }
    private static void CancelSafely(CancellationTokenSource? source)
    {
        // Cancellation may synchronously run the owner's finally block. Never retain a shared
        // reference while cancelling it; only the asynchronous owner disposes the source.
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private void CancelActiveGeneration()
    {
        var source = _generationCancellation;
        _generationCancellation = null; _generationTask = null; _generationKey = null;
        CancelSafely(source);
        if (!_stopping) _generate.IsEnabled = true;
    }
    private void CancelGeneration()
    { CancelAutoDelay(); CancelActiveGeneration(); }
    private void QueueAutoGeneration()
    {
        if (_stopping || _conversationPending || _refreshingSelection || _automatic.IsChecked != true || _selected is null || !_focus.Contains(_selected) ||
            _replyProduct.SelectedItem is not CatalogProduct selected) { CancelAutoDelay(); return; }
        if (!_autoEligible.Contains(_selected.MessageId) || SellerHasReplied(_selected)) { CancelAutoDelay(); return; }
        if (_focus.Messages(_messages).FirstOrDefault(m => m.Role == "buyer")?.MessageId != _selected.MessageId) { CancelAutoDelay(); return; }
        var product = _shared.Products().SingleOrDefault(p => p.SellerId == SellerId && p.ProductId == selected.ProductId);
        if (product?.Approved != true) { CancelAutoDelay(); return; }
        try { PrepareSelectedContext(); }
        catch (Exception error) when (error is InvalidOperationException or System.IO.InvalidDataException)
        {
            _shared.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ContextUnavailable,
                new() { WindowId = ScopeId, SessionId = _selected?.SessionId, MessageId = _selected?.MessageId }, error);
            CancelAutoDelay(); _replyState.Text = "当前会话尚未准备好"; SetStatus(error.Message); return;
        }
        var key = WorkKey(_selected, product);
        if (_autoAttempted.Contains(key) || _scheduledKey == key || _generationKey == key) return;
        var existing = _replies.Latest(_selected);
        if (existing?.CurrentProductId == product.ProductId && existing.EditedReply is not null) return;
        if (existing?.CurrentProductId == product.ProductId && _replies.IsCurrentPipeline(existing) &&
            (existing.Status is "Approved" or "Copied" || existing.EditedReply is not null ||
                existing.ProductKnowledgeVersion == product.UpdatedAt.ToString("O") &&
                (_shared.Shop(SellerId)?.UpdatedAt ?? DateTimeOffset.MinValue) <= existing.CreatedAt)) return;
        if (_reply.Text.Length > 0 && (_view is null || _reply.Text != ReplyTextPolicy.Normalize(_view.EffectiveReply))) return;
        CancelAutoDelay();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _autoDelay = source; _scheduledKey = key;
        _ = AutoWaitAsync(key, source);
    }
    private async Task AutoWaitAsync(string key, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(500, source.Token);
            if (_stopping || _conversationPending || _automatic.IsChecked != true || _selected is null || !_focus.Contains(_selected) || SellerHasReplied(_selected) ||
                _replyProduct.SelectedItem is not CatalogProduct product ||
                WorkKey(_selected, _shared.Products().SingleOrDefault(p => p.SellerId == SellerId && p.ProductId == product.ProductId)) != key) return;
            _autoAttempted.Add(key);
            if (_autoAttempted.Count > 500) _autoAttempted.Remove(_autoAttempted.First());
            await GenerateCurrentAsync();
        }
        catch (OperationCanceledException)
        {
            _autoAttempted.Remove(key);
            _shared.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ReplyCancelled, new() { WindowId = ScopeId });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _shared.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed,
                new() { WindowId = ScopeId, SessionId = _selected?.SessionId, MessageId = _selected?.MessageId }, error);
            SetStatus("自动生成未完成，可点击重新生成：" + Friendly(error));
        }
        finally
        {
            if (ReferenceEquals(_autoDelay, source)) { _autoDelay = null; _scheduledKey = null; }
            source.Dispose();
        }
    }
    private QianniuConnection.PreparedReply PrepareSelectedContext()
    {
        var message = _selected ?? throw new InvalidOperationException("请先读取当前买家消息。");
        if (!_focus.Contains(message)) throw new InvalidOperationException("买家已切换，请等待识别完成。");
        if (_conversationPending) throw new InvalidOperationException("正在更新对话，请稍候再生成。");
        var prepared = _connection.PrepareCurrentReply(message, _focus.SellerId, _focus.Buyer);
        if (prepared.Message.SessionId != message.SessionId)
        {
            AcceptMessage(prepared.Message); message = prepared.Message; _selected = message;
            _refreshingSelection = true;
            try { _inbox.ItemsSource = _focus.Messages(_messages).Reverse().ToArray(); _inbox.SelectedItem = message; }
            finally { _refreshingSelection = false; }
            _view = null; _callStatus.Text = "当前买家会话已恢复，正在重新生成";
        }
        return prepared;
    }
    private async Task GenerateCurrentAsync()
    {
        var prepared = PrepareSelectedContext();
        var message = prepared.Message;
        var selected = _replyProduct.SelectedItem as CatalogProduct;
        var product = selected is null ? null : _shared.Products().SingleOrDefault(p => p.SellerId == SellerId && p.ProductId == selected.ProductId)
            ?? throw new InvalidOperationException("商品资料已变化，请重新选择。");
        var key = WorkKey(message, product);
        if (_generationKey == key && _generationTask is not null) { await _generationTask; return; }
        CancelActiveGeneration();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _generationCancellation = source; _generationKey = key;
        var epoch = _focus.Revision; var dialogueRevision = _dialogueRevision; var originalDraft = _reply.Text;
        Task<AssistedReplyView>? task = null;
        try
        {
            _generate.IsEnabled = false; SetStatus("正在识别意图、生成回复并核验依据…");
            ResetIntentCard("正在分析当前买家消息…");
            // Context/settings lookup can throw synchronously, before any model Task exists.
            // It must be inside the cleanup scope, or the shared source remains disposed and the button stays disabled.
            var generationToken = source.Token;
            task = _replies.GenerateAsync(message, prepared.Context, product, Settings, generationToken, analysis =>
            {
                void UpdateIntent()
                {
                    if (!_stopping && !_conversationPending && !generationToken.IsCancellationRequested && dialogueRevision == _dialogueRevision && _selected?.MessageId == message.MessageId &&
                        _focus.Revision == epoch && (_replyProduct.SelectedItem as CatalogProduct)?.ProductId == product?.ProductId)
                        ShowIntent(analysis);
                }
                if (_dispatcher.CheckAccess()) UpdateIntent(); else _ = _dispatcher.BeginInvoke(UpdateIntent);
            });
            _generationTask = task; _operations.Add(task);
            var view = await task;
            while (_conversationPending && !source.IsCancellationRequested && !_stopping) await Task.Delay(50, source.Token);
            if (_stopping || source.IsCancellationRequested || dialogueRevision != _dialogueRevision || _selected?.MessageId != message.MessageId || _focus.Revision != epoch ||
                (_replyProduct.SelectedItem as CatalogProduct)?.ProductId != product?.ProductId) return;
            var currentProduct = product is null ? null : _shared.Products().SingleOrDefault(p => p.SellerId == SellerId && p.ProductId == product.ProductId);
            if (WorkKey(message, currentProduct) != key || currentProduct is { Approved: false })
            { SetStatus("商品资料已变化，旧资料生成的建议未显示，请使用最新已核对资料。"); _ = _dispatcher.BeginInvoke(QueueAutoGeneration); return; }
            bool editedWhileWaiting = _reply.Text != originalDraft;
            if (editedWhileWaiting) { _view = view; ShowCallStatus(view); ShowIntentForReply(view); } else ShowReply(view);
            SetStatus(IsServiceFailure(view) ? "本次未生成有效建议，请重试或手工填写回复。"
                : editedWhileWaiting ? "新建议已保存，保留了你正在编辑的文字。" : "回复建议已生成，请核对后确认并复制。");
            _autoAttempted.Add(key);
            _autoEligible.Remove(message.MessageId);
        }
        finally
        {
            if (task is not null) _operations.Remove(task);
            if (ReferenceEquals(_generationCancellation, source))
            { _generationCancellation = null; _generationTask = null; _generationKey = null; if (!_stopping) _generate.IsEnabled = true; }
        }
    }
}
