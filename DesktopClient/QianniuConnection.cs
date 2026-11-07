using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Capture;
using QnocrPoc.Detection;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Product;
using QnocrPoc.Shadow;

namespace QNAgent;

internal sealed class ConversationChangedException(string message) : InvalidOperationException(message);

internal sealed class QianniuConnection : IDisposable
{
    private readonly ClientStore _store;
    private readonly string _windowScopeId;
    private readonly AgentContextPipeline _context;
    private readonly object _contextGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private long _interactionVersion;
    private QianniuLayout? _layout;
    private string _sellerId = "";
    private QianniuWindow? _captureWindow;
    private (long Version, string Seller, string Buyer, string Fingerprint)? _productFrame;
    private string? _confirmedBuyer, _confirmedIdentityFingerprint;
    private long _confirmedInteraction = -1;
    private BuyerMessage[] _visibleDialogue = Array.Empty<BuyerMessage>();
    internal Task<bool> WaitForWakeAsync(TimeSpan delay, CancellationToken token) => _wake.WaitAsync(delay, token);
    public bool IsCurrent(ObservedConversation observation) => observation.CaptureVersion is null ||
        observation.CaptureVersion == Interlocked.Read(ref _interactionVersion);
    public System.Drawing.Rectangle? BuyerListBounds => Volatile.Read(ref _layout)?.BuyerListRegion;
    public void NotifyConversationClick()
    {
        Interlocked.Increment(ref _interactionVersion);
        lock (_contextGate) _productFrame = null;
        Conversation?.Invoke(new(_sellerId, null, "正在切换买家…"));
        if (_wake.CurrentCount == 0) _wake.Release();
    }
    public async Task<string?> RecognizeCurrentProductAsync(InboxMessage message, CancellationToken token)
    {
        (long Version, string Seller, string Buyer, string Fingerprint) frame;
        lock (_contextGate)
        {
            if (_productFrame is not { } current || current.Seller != message.SellerId ||
                new IdentityNormalizer().Normalize(current.Buyer) != new IdentityNormalizer().Normalize(message.Buyer))
                throw new ConversationChangedException("当前会话已变化，请等待买家识别完成。");
            frame = current;
        }
        await using var worker = await MemoryNativeWorker.StartAsync(token);
        var target = Volatile.Read(ref _captureWindow) ?? throw new InvalidOperationException("千牛尚未连接。");
        var capture = await worker.CaptureAsync(target, token);
        if (capture.Png is null) throw new InvalidOperationException("请恢复千牛窗口后再识别商品。");
        var image = MemoryChatFrame.FromPng(capture.Png);
        if (image.ChatFingerprint != frame.Fingerprint) throw new ConversationChangedException("会话画面已变化，请等待当前买家识别完成。");
        var ocr = await worker.RecognizeAsync(image.Product, "memory:product-on-demand", token);
        lock (_contextGate)
            if (_productFrame is not { } current || current.Version != frame.Version ||
                current.Seller != frame.Seller || current.Buyer != frame.Buyer || current.Fingerprint != frame.Fingerprint ||
                frame.Version != Interlocked.Read(ref _interactionVersion))
                throw new ConversationChangedException("商品识别期间会话已变化，请重新识别。");
        var product = new ProductIdExtractor().Extract(ocr);
        return product.Status == ProductIdStatus.Identified && product.Confidence >= .80 ? product.ProductId : null;
    }
    public event Action<string>? Status;
    public event Action<InboxMessage>? Message;
    public event Action<byte[]>? Screenshot;
    public event Action<ObservedConversation>? Conversation;
    public event Action<string, byte[]>? MessagePreview;
    public QianniuConnection(ClientStore store, string windowScopeId = "", TimeProvider? timeProvider = null)
    {
        _store = store;
        _windowScopeId = windowScopeId;
        _context = new AgentContextPipeline(Path.Combine(store.Root, "context.db"), timeProvider);
    }
    public static IReadOnlyList<QianniuWindow> Windows() => new QianniuWindowCapture().FindWindows(_ => { })
        .Where(window => window.WindowTitle.Contains("接待中心", StringComparison.Ordinal)).ToArray();
    public SessionContextView Context(InboxMessage message)
    {
        lock (_contextGate)
        {
            var session = _context.GetSession(message.SessionId) ?? throw new InvalidOperationException("当前会话已不在可用上下文中，请重新采集。");
            return AgentContextIntentAdapter.Build(session, message.MessageId, DateTimeOffset.Now, maxRecentMessages: 9, maxContextCharacters: 6000, orderByTimestamp: true);
        }
    }
    internal sealed record PreparedReply(InboxMessage Message, SessionContextView Context);
    internal PreparedReply PrepareCurrentReply(InboxMessage message, string observedSeller, string? observedBuyer)
    {
        var normalizer = new IdentityNormalizer();
        if (message.Role != "buyer" || message.WindowScopeId != _windowScopeId || message.SellerId != observedSeller || string.IsNullOrWhiteSpace(observedBuyer) ||
            normalizer.Normalize(message.Buyer) != normalizer.Normalize(observedBuyer))
            throw new InvalidOperationException("当前买家或窗口已变化，请等待重新识别。");
        lock (_contextGate)
        {
            if (_captureWindow is not null && (_productFrame is not { } frame || frame.Seller != observedSeller ||
                normalizer.Normalize(frame.Buyer) != normalizer.Normalize(observedBuyer) || frame.Version != Interlocked.Read(ref _interactionVersion)))
                throw new InvalidOperationException("正在确认当前买家，请稍候再生成回复。");
            var original = _context.GetSession(message.SessionId) ?? throw new InvalidOperationException("当前消息上下文不存在，请重新读取聊天画面。");
            if (original.Identity.Source != "Qianniu.OCR" || original.Identity.SellerId != message.SellerId ||
                original.Identity.BuyerId != normalizer.Normalize(message.Buyer) || original.Messages.Any(m => m.SessionId != original.SessionId || m.Source != original.Identity.Source))
                throw new InvalidDataException("消息与当前买家或店铺不一致，已停止生成。");
            var selected = original.Messages.Where(m => m.MessageId == message.MessageId).ToArray();
            if (selected.Length != 1 || selected[0].Role != "buyer" || selected[0].Timestamp != message.Timestamp ||
                !(selected[0].Content == message.Text || message.ContentKind == "Unrecognized" && selected[0].Content.StartsWith(message.Text + " [图像消息标识：", StringComparison.Ordinal)))
                throw new InvalidDataException("当前消息与会话记录不一致，请重新读取。");
            var current = original;
            if (current.Status != AgentSessionStatus.Active)
            {
                // Re-observe only the selected, currently visible message. Do not reactivate an
                // expired session or copy its history; the manager selects/creates an isolated Active session.
                var item = selected[0];
                var observed = Array.Empty<BuyerMessage>();
                if (_productFrame is { } verified && verified.Seller == message.SellerId && normalizer.Normalize(verified.Buyer) == original.Identity.BuyerId)
                {
                    var at = Array.FindIndex(_visibleDialogue, m => m.Role == "buyer" && m.Timestamp == item.Timestamp && ObservedContent(m) == item.Content);
                    if (at >= 0) observed = _visibleDialogue.Take(at + 1).TakeLast(10).ToArray();
                }
                var seed = observed.Length > 0 ? observed.Select(m => new EnvelopeMessage(m.Role, ObservedContent(m), m.Timestamp)).ToArray()
                    : new[] { new EnvelopeMessage(item.Role, item.Content, item.Timestamp) };
                current = _context.ProcessMessage(new MessageEnvelope(original.Identity.Source, message.SellerId, original.Identity.BuyerId, message.ProductId, seed));
                if (!current.Messages.Any(m => m.MessageId == message.MessageId))
                    throw new InvalidDataException("会话恢复后的消息标识不一致，请重新读取。");
                _store.Write("last-context-refresh.json", new { PreviousSessionId = original.SessionId, CurrentSessionId = current.SessionId,
                    message.MessageId, PreviousStatus = original.Status.ToString(), Reason = "CurrentVisibleMessage", RefreshedAt = DateTimeOffset.Now });
                _store.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.SessionRecovered,
                    new() { WindowId = _windowScopeId, SessionId = current.SessionId, MessageId = message.MessageId, Count = seed.Length, Success = true });
            }
            var rebound = message with { SessionId = current.SessionId };
            return new(rebound, AgentContextIntentAdapter.Build(current, rebound.MessageId, DateTimeOffset.Now, maxRecentMessages: 9, maxContextCharacters: 6000, orderByTimestamp: true));
        }
    }
    private static string ObservedContent(BuyerMessage message) => message.ContentKind == "Unrecognized"
        ? message.Text + " [图像消息标识：" + message.VisualFingerprint + "]" : message.Text;
    internal string? ResolveObservedBuyer(BuyerMessageBatch batch, OcrResult ocr, string identityFingerprint, long interaction)
    {
        var buyer = SessionRegistry.ReadBuyer(batch, ocr);
        if (buyer is null && batch.Messages.Count > 0 && batch.Messages.All(m => m.Role == "seller") &&
            _confirmedIdentityFingerprint == identityFingerprint && _confirmedInteraction == interaction) return _confirmedBuyer;
        return buyer;
    }
    public async Task RunAsync(QianniuWindow selected, ClientSettings settings, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.SellerId)) throw new InvalidOperationException("请先填写当前接待窗口对应的店铺标识。");
        _sellerId = settings.SellerId;
        Volatile.Write(ref _captureWindow, selected);
        Interlocked.Increment(ref _interactionVersion);
        var threads = settings.FastRecognition && Environment.ProcessorCount >= 8 ? 2 : 1;
        await using var native = await MemoryNativeWorker.StartAsync(token, threads);
        var trackers = new Dictionary<string, NewMessageTracker>(StringComparer.Ordinal);
        var known = _store.Messages().Where(message => _windowScopeId.Length == 0 || message.WindowScopeId == _windowScopeId)
            .GroupBy(message => message.MessageId).ToDictionary(g => g.Key, g => g.Last().SessionId, StringComparer.Ordinal);
        var cache = new ChatRecognitionCache();
        var previewIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var gate = new SettledFrameGate(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1500));
        var clock = Stopwatch.StartNew();
        var interval = Math.Clamp(settings.CaptureIntervalMilliseconds, 250, 2000);
        MemoryChatFrame? lastFrame = null;
        string? lastChatHash = null;
        var lastChatChange = clock.Elapsed;
        var fastUntil = clock.Elapsed + TimeSpan.FromSeconds(1);
        string? processedHash = null;
        bool waiting = false;
        var observedInteraction = Interlocked.Read(ref _interactionVersion);
        lock (_contextGate) _productFrame = null;
        Status?.Invoke("正在连接：等待有效画面与消息识别（图片仅在内存中处理）…");
        while (!token.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            try
            {
                var interaction = Interlocked.Read(ref _interactionVersion);
                if (interaction != observedInteraction)
                { gate.Reset(); lastChatHash = null; lastChatChange = clock.Elapsed; fastUntil = clock.Elapsed + TimeSpan.FromSeconds(1); observedInteraction = interaction; }
                var capture = await native.CaptureAsync(selected, token);
                if (capture.Waiting)
                {
                    if (!waiting)
                    {
                        Status?.Invoke("等待千牛：请显示接待中心窗口，不能最小化。");
                        Conversation?.Invoke(new(settings.SellerId, null, "千牛窗口已隐藏，等待恢复"));
                    }
                    waiting = true; gate.Reset();
                    lock (_contextGate) _productFrame = null;
                    await Task.Delay(interval, token); continue;
                }
                waiting = false;
                if (!capture.Unchanged)
                {
                    var png = capture.Png ?? throw new IOException("千牛截图失败或为空白，请将接待窗口恢复显示。");
                    Screenshot?.Invoke(png);
                    lastFrame = MemoryChatFrame.FromPng(png, includeProduct: false);
                    Volatile.Write(ref _layout, lastFrame.Layout);
                }
                var frame = lastFrame ?? throw new IOException("采集进程缺少首次有效画面。");
                var chat = frame.Chat;
                // Product carousels/status animations alone are not new chat messages.
                // The current product pixels are still read from the same frame on each chat change.
                var hash = frame.ChatFingerprint;
                if (hash != lastChatHash)
                {
                    if (hash == processedHash) gate.Reset(); // restore an already processed frame after a transient change
                    if (clock.Elapsed - lastChatChange > TimeSpan.FromSeconds(2)) fastUntil = clock.Elapsed + TimeSpan.FromSeconds(1);
                    lastChatHash = hash; lastChatChange = clock.Elapsed;
                    lock (_contextGate) _productFrame = null;
                    var sameIdentity = _confirmedBuyer is not null && _confirmedIdentityFingerprint == frame.IdentityFingerprint && _confirmedInteraction == interaction;
                    _store.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ConversationPending,
                        new() { WindowId = _windowScopeId, Code = sameIdentity ? "BodyChange" : "IdentityChange" });
                    Conversation?.Invoke(new(settings.SellerId, null, sameIdentity ? "正在更新对话上下文…" : "正在确认当前买家…", interaction, PreserveDisplay: sameIdentity));
                }
                if (gate.Observe(hash, clock.Elapsed))
                {
                    var captureId = "memory:" + Guid.NewGuid().ToString("N");
                    var cached = cache.TryGet(hash, out var ocr);
                    if (!cached)
                    {
                        ocr = await native.RecognizeChatAsync(chat, captureId + "/chat", token);
                        // Seller bubbles alone used to prevent a retry even when the buyer header
                        // was missing. Retry the full region once, without weakening identity checks.
                        var focusedBatch = new BuyerMessageStructurer(true, true).Structure(ocr, chat);
                        if (ResolveObservedBuyer(focusedBatch, ocr, frame.IdentityFingerprint, interaction) is null)
                        {
                            var full = await native.RecognizeAsync(chat, captureId + "/chat-full", token);
                            var fullBatch = new BuyerMessageStructurer(true, true).Structure(full, chat);
                            if (ResolveObservedBuyer(fullBatch, full, frame.IdentityFingerprint, interaction) is not null ||
                                focusedBatch.Messages.Count == 0 && fullBatch.Messages.Count > 0) ocr = full;
                            _store.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ConversationPending,
                                new() { WindowId = _windowScopeId, Code = "FullChatOcrFallback", Count = fullBatch.Messages.Count });
                        }
                        cache.Add(hash, ocr);
                    }
                    if (interaction != Interlocked.Read(ref _interactionVersion)) continue;
                    // OCR can outlive a keyboard-driven switch; verify before publishing its result.
                    if (!cached)
                    {
                        var verify = await native.CaptureAsync(selected, token);
                        if (verify.Waiting) { gate.Reset(); continue; }
                        if (!verify.Unchanged)
                        {
                            var latest = verify.Png ?? throw new IOException("确认当前会话时未取得画面。");
                            var currentFrame = MemoryChatFrame.FromPng(latest, includeProduct: false);
                            lastFrame = currentFrame; Screenshot?.Invoke(latest);
                            Volatile.Write(ref _layout, currentFrame.Layout);
                            if (currentFrame.ChatFingerprint != hash) { gate.Reset(); continue; }
                        }
                    }
                    if (interaction != Interlocked.Read(ref _interactionVersion)) continue;
                    var batch = new BuyerMessageStructurer(true, true).Structure(ocr, chat);
                    var buyer = ResolveObservedBuyer(batch, ocr, frame.IdentityFingerprint, interaction);
                    // Seller-only viewport may no longer contain an incoming sender header. Reuse a
                    // previously confirmed buyer only while the exact header pixels and interaction epoch match.
                    if (string.IsNullOrWhiteSpace(buyer))
                    {
                        var missingHeader = batch.ReviewItems.Any(r => r.Reason == "MissingReliableFullTimestampAndBuyerHeader");
                        _store.Diagnostics.Write(DiagnosticLogLevel.Warning, DiagnosticEvent.RecognitionHeld, new()
                        {
                            WindowId = _windowScopeId, Count = batch.Messages.Count, SellerMessages = batch.Messages.Count(m => m.Role == "seller"),
                            Code = missingHeader ? "TimestampHeaderMissing" : "NoUniqueBuyer"
                        });
                        var detail = missingHeader ? "昵称或完整时间头尚未确认，等待会话画面稳定" : "尚未确认唯一买家，等待会话画面稳定";
                        Status?.Invoke("已取得千牛画面；" + detail + "。可在日志与诊断中查看识别记录。");
                        Conversation?.Invoke(new(settings.SellerId, null, detail));
                    }
                    else
                    {
                        var label = new IdentityNormalizer().Normalize(buyer);
                        if (!trackers.TryGetValue(label, out var tracker)) trackers[label] = tracker = new NewMessageTracker(DateTime.Now);
                        var novelty = tracker.Compare(batch.Messages, DateTime.Now);
                        _confirmedBuyer = buyer; _confirmedIdentityFingerprint = frame.IdentityFingerprint; _confirmedInteraction = interaction;
                        // Product OCR is explicitly requested from the panel; it never blocks buyer/message publication.
                        const string productId = "";
                        var accepted = batch.Messages.Where((_, i) => novelty.Decisions[i].Disposition != "FutureTimestampReview").ToArray();
                        lock (_contextGate)
                            if (interaction == Interlocked.Read(ref _interactionVersion))
                            { _productFrame = (interaction, settings.SellerId, buyer, hash); _visibleDialogue = accepted; }
                        AgentSession? session = null;
                        if (accepted.Length > 0)
                            lock (_contextGate) session = _context.ProcessMessage(new MessageEnvelope("Qianniu.OCR", settings.SellerId, label, productId,
                                accepted.Select(m => new EnvelopeMessage(m.Role, ObservedContent(m), m.Timestamp)).ToArray()));
                        var freshKeys = novelty.NewMessages.Select(MessageFingerprint.ConversationMessageKey).ToHashSet(StringComparer.Ordinal);
                        foreach (var message in accepted)
                        {
                            var content = ObservedContent(message);
                            var stored = session!.Messages.Last(item => item.Role == message.Role && item.Content == content && item.Timestamp == message.Timestamp);
                            previewIds[label + "\0" + MessageFingerprint.ConversationMessageKey(message)] = stored.MessageId;
                            var sameBinding = known.TryGetValue(stored.MessageId, out var knownSession) && knownSession == session.SessionId;
                            known[stored.MessageId] = session.SessionId;
                            if (sameBinding && message.ContentKind == "Text") continue;
                            _store.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.MessageObserved,
                                new() { WindowId = _windowScopeId, SessionId = session.SessionId, MessageId = stored.MessageId, Role = message.Role, Count = message.Text.Length });
                            Message?.Invoke(new InboxMessage(session.SessionId, stored.MessageId, settings.SellerId, buyer,
                                message.Text, message.Timestamp, productId, captureId, !freshKeys.Contains(MessageFingerprint.ConversationMessageKey(message)))
                                { ContentKind = message.ContentKind, WindowScopeId = _windowScopeId, Role = message.Role,
                                    Sequence = session.Messages.Select((item, index) => (item, index)).Single(p => p.item.MessageId == stored.MessageId).index + 1 });
                        }
                        foreach (var message in batch.Messages)
                            if (message.PreviewPng is { } preview && previewIds.TryGetValue(label + "\0" + MessageFingerprint.ConversationMessageKey(message), out var id))
                                MessagePreview?.Invoke(id, preview);
                        while (previewIds.Count > 512) previewIds.Remove(previewIds.Keys.First());
                        if (interaction == Interlocked.Read(ref _interactionVersion))
                        {
                            _store.Diagnostics.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.ConversationConfirmed,
                                new() { WindowId = _windowScopeId, Count = batch.Messages.Count, Success = true });
                            Status?.Invoke($"千牛已连接 · 买家 {buyer} · {(novelty.IsBaseline ? "当前可见消息已载入" : $"本次新消息 {novelty.NewMessages.Count} 条")} · {DateTime.Now:HH:mm:ss}");
                            Conversation?.Invoke(new(settings.SellerId, buyer, "当前千牛会话 · 回复需人工确认", interaction));
                        }
                    }
                    // Even a valid frame without a confirmed buyer is complete: retry on change,
                    // rather than repeatedly running OCR against identical pixels.
                    gate.Complete(hash);
                    processedHash = hash;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _store.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.CaptureFailed, new() { WindowId = _windowScopeId }, error);
                Status?.Invoke("连接中断：" + error.Message);
                Conversation?.Invoke(new(settings.SellerId, null, "连接已中断，请打开设置重新连接"));
                return;
            }
            // Still sample while idle, so the next message wakes normal cadence automatically.
            // PrintWindow also asks the target to render; avoid doing this twice a second forever.
            var idle = clock.Elapsed - lastChatChange;
            var effectiveInterval = idle.TotalSeconds >= 15 ? Math.Max(interval, 2000)
                : idle.TotalSeconds >= 5 ? Math.Max(interval, 1000) : interval;
            // The second stability sample should not wait an idle/configured polling interval.
            if (clock.Elapsed < fastUntil) effectiveInterval = Math.Min(effectiveInterval, 120);
            var remaining = TimeSpan.FromMilliseconds(effectiveInterval) - (clock.Elapsed - started);
            if (remaining > TimeSpan.Zero) await WaitForWakeAsync(remaining, token);
        }
    }
    public void Dispose() { lock (_contextGate) _context.Dispose(); }
}
