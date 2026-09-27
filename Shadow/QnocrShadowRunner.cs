using System.Diagnostics;
using System.Text.Json;
using QnocrPoc.Adapter;
using QnocrPoc.Capture;
using QnocrPoc.Detection;
using QnocrPoc.Layout;
using QnocrPoc.Knowledge;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Product;

namespace QnocrPoc.Shadow;

public sealed record ShadowRunOptions(int? MaximumFrames = null, int IntervalMilliseconds = 1000, string? IdentityMappingPath = null,
    bool AiShadow = false, double? MaximumMinutes = null, string? StateDirectory = null, string? AiSessionId = null,
    string? AiProviderConfigPath = null, double MaxRunMegabytes = 1024, double MaxStateMegabytes = 4096,
    string? ProductKnowledgePath = null, string? KnowledgeWorkflowItemsPath = null);
public sealed record ShadowRunSummary(string Status, int Attempts, int SuccessfulFrames, int FailedFrames,
    int ProductOcrCalls, int ChatOcrCalls, int Envelopes, int ConvertedEnvelopes, int DeferredEnvelopes, string SessionDirectory,
    int NewMessages, int BaselineMessages, int DuplicateObservations, int HistoricalObservations, int ConvertedMessages,
    bool AiEnabled = false, int AiInvokedResults = 0, int ReplyPreviews = 0, double ElapsedSeconds = 0,
    int BuyerSessions = 0, int UncertainMessages = 0, int RecoveryEvents = 0, int JournalErrors = 0, string? StateDirectory = null,
    int OutOfScopeMessages = 0, int EmptyReplies = 0, int AiErrors = 0, int AiCalls = 0,
    bool SendEnabled = false, int SentMessages = 0, DateTimeOffset StartedAt = default, DateTimeOffset EndedAt = default,
    long PeakWorkingSetBytes = 0, int ProductReferences = 0, int ActiveSessions = 0, int InactiveSessions = 0,
    int ClosedSessions = 0, int ReopenedSessions = 0, int ConfirmedObservations = 0, int ErrorCount = 0,
    long RunBytes = 0, long StateBytes = 0, int LogArchiveSegments = 0, int PersistedSessions = 0, int KnowledgeErrors = 0, bool ProductKnowledgeEnabled = false);

public sealed class QnocrShadowRunner
{
    private readonly Func<string, Task<CaptureResult>>? _capture;
    private readonly Func<string, QianniuWindow?, Task<ResolvedLayout>> _layout;
    private readonly Func<string, OcrResult>? _ocr;
    private readonly Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>>? _adapt;
    private readonly Func<DateTime> _displayNow;
    private readonly IKnowledgeProvider? _knowledgeProvider;
    public QnocrShadowRunner(Func<string, Task<CaptureResult>>? capture = null,
        Func<string, QianniuWindow?, Task<ResolvedLayout>>? layout = null, Func<string, OcrResult>? ocr = null,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>>? adapt = null, Func<DateTime>? displayNow = null,
        IKnowledgeProvider? knowledgeProvider = null)
    {
        _capture = capture; _layout = layout ?? new QianniuLayoutResolver().ResolveAsync; _ocr = ocr; _adapt = adapt;
        _displayNow = displayNow ?? (() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")));
        _knowledgeProvider = knowledgeProvider;
    }

    public async Task<ShadowRunSummary> RunAsync(ShadowRunOptions options, CancellationToken token = default)
    {
        if (options.MaximumFrames is <= 0 || options.IntervalMilliseconds is < 0 or > 60000 || options.MaximumMinutes is <= 0
            || options.MaximumMinutes is { } minutes && !double.IsFinite(minutes)
            || !double.IsFinite(options.MaxRunMegabytes) || options.MaxRunMegabytes <= 0
            || !double.IsFinite(options.MaxStateMegabytes) || options.MaxStateMegabytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        token.ThrowIfCancellationRequested();
        if (options.AiShadow && options.IdentityMappingPath is null) throw new InvalidDataException("AI Shadow requires an explicit test mapping.");
        if (options.ProductKnowledgePath is not null && !options.AiShadow)
            throw new InvalidDataException("Local test product knowledge requires explicit AI Shadow mode.");
        if (options.ProductKnowledgePath is not null && _knowledgeProvider is not null)
            throw new InvalidDataException("Choose either an injected provider or a local test file, never both.");
        if (options.KnowledgeWorkflowItemsPath is not null &&
            (options.ProductKnowledgePath is null || !File.Exists(options.KnowledgeWorkflowItemsPath)))
            throw new InvalidDataException("Knowledge workflow requires an existing state file and explicit test product source.");
        if (options.AiShadow && (options.AiSessionId is null || options.AiSessionId.Length != 64 || !options.AiSessionId.All(Uri.IsHexDigit)))
            throw new InvalidDataException("AI Shadow requires the 64-character SessionId of the explicit test buyer.");
        var allowedAiSession = options.AiSessionId?.ToUpperInvariant();
        var run = Path.GetFullPath(Path.Combine("Output", "Shadow", $"shadow_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(run);
        var stateRoot = Path.GetFullPath(options.StateDirectory ?? Path.Combine("Output", "State", "V14"));
        string? mapping = null;
        if (options.IdentityMappingPath is not null)
        {
            var json = File.ReadAllText(options.IdentityMappingPath);
            using var doc = JsonDocument.Parse(json);
            if (string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("SellerId").GetString()) || string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("BuyerId").GetString()))
                throw new InvalidDataException("Explicit identity mapping needs seller/buyer IDs.");
            if (options.AiShadow && !doc.RootElement.GetProperty("IsTestMapping").GetBoolean()) throw new InvalidDataException("AI Shadow requires isolated test identity.");
            mapping = Path.Combine(run, "identity.snapshot.json"); File.WriteAllText(mapping, json);
        }
        var selectedKnowledge = options.ProductKnowledgePath is null ? _knowledgeProvider :
            new ProductKnowledgeProvider(new ProductKnowledgeRepository(
                new LocalFileKnowledgeSource(options.ProductKnowledgePath, options.KnowledgeWorkflowItemsPath)));
        var registry = new SessionRegistry(stateRoot, run, mapping);
        var lifecycle = new SessionRunLifecycle(stateRoot, run);
        var agentTrace = new AgentPipelineTrace(run);
        var native = new NativeWorkerClient();
        var windowCapture = new QianniuWindowCapture();
        var profiles = new ProductContextResolver(Path.Combine(AppContext.BaseDirectory, "Product", "product-profiles.sample.json"));
        var detector = new ChatRegionChangeDetector(); var extractor = new QianniuRegionExtractor(); var structurer = new BuyerMessageStructurer();
        using var dispatch = new DurableShadowDispatch(stateRoot, Path.Combine(run, "lifecycle.jsonl"), token,
            _adapt ?? (options.AiShadow
                ? new AgentShadowClient(options.AiProviderConfigPath).ConvertAsync
                : new ShadowAdapterClient(dispatch: true).ConvertAsync), options.AiShadow, agentTrace,
            knowledgeProvider: selectedKnowledge, enrichAiContext: options.ProductKnowledgePath is not null);
        dispatch.Start();
        var startedAt = DateTimeOffset.Now;
        var watch = Stopwatch.StartNew(); var nextHeartbeat = TimeSpan.Zero; var peakWorkingSet = 0L; var confirmedObservations = 0;
        QianniuWindow? window = null; string? selectedTitle = null; string? previousChat = null;
        QianniuLayout? previousLayout = null;
        var attempts = 0; var frames = 0; var failures = 0; var productCalls = 0; var chatCalls = 0; var baselines = 0; var duplicates = 0; var historical = 0; var recoveries = 0; var consecutiveFailures = 0;
        var runIds = new HashSet<string>(StringComparer.Ordinal); var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        var outOfScopeMessages = 0;
        var status = "Completed";
        AtomicJson.Write(Path.Combine(run, "session.json"), new { Version = "1.26", StartedAt = startedAt, StateDirectory = stateRoot,
            options.MaximumFrames, options.MaximumMinutes, options.MaxRunMegabytes, options.MaxStateMegabytes, AiEnabled = options.AiShadow, SendEnabled = false,
            AllowedAiSession = allowedAiSession, ProviderConfig = options.AiProviderConfigPath ?? "Config/ai-provider.json",
            Identity = "Per-buyer OCR label sessions; test identities are isolated, no platform identity inference",
            ProductKnowledgeEnabled = options.ProductKnowledgePath is not null });
        Console.WriteLine($"[SHADOW V1.26] Session: {run}\nState: {stateRoot}\nAI={(options.AiShadow ? "ON" : "OFF")}; Send=OFF; first buyer snapshot is baseline. stop.request stops cleanly.");
        Console.WriteLine($"[Shadow Flags] AI_ENABLED={options.AiShadow.ToString().ToLowerInvariant()} SEND_ENABLED=false");
        try
        {
            while (options.MaximumFrames is null || attempts < options.MaximumFrames)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(run, "stop.request"))) { status = "StoppedByRequest"; break; }
                if (options.MaximumMinutes is { } limit && watch.Elapsed.TotalMinutes >= limit) break;
                attempts++;
                var captureTime = DateTimeOffset.Now;
                string? fullPath = null;
                NewMessageTracker? tracker = null; NewMessageComparison? comparison = null;
                try
                {
                    var slot = Path.Combine(run, "scratch", "slot_" + attempts % 2); Directory.CreateDirectory(slot);
                    CaptureResult captured;
                    if (_capture is not null) captured = await _capture(slot);
                    else
                    {
                        if (window is null)
                        {
                            var candidates = windowCapture.FindWindows(_ => { }).Where(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked
                                && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal) && (selectedTitle is null || w.WindowTitle == selectedTitle)).ToArray();
                            if (candidates.Length != 1) throw new InvalidOperationException($"Reception center unavailable/ambiguous ({candidates.Length}); waiting without switching accounts.");
                            window = candidates[0]; selectedTitle ??= window.WindowTitle;
                            SafeAppend(run, "recovery.jsonl", new { At = DateTimeOffset.Now, Stage = "WindowBound", window.Pid, Hwnd = $"0x{window.Hwnd:X}", window.WindowTitle });
                        }
                        captured = await native.CaptureAsync(window, slot, token);
                    }
                    fullPath = captured.SavedPath;
                    if (!captured.Success || fullPath is null) throw new IOException(captured.Message);
                    ResolvedLayout layout;
                    try { layout = await _layout(fullPath, window); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        recoveries++;
                        layout = new QianniuLayoutResolver().ResolveWithGeometry(fullPath, UiaGeometryProbe.Unavailable("Layout probe failed; geometry-free fallback: " + ex.GetType().Name));
                    }
                    var pixels = layout.ToPixelLayout(); extractor.Extract(fullPath, slot, pixels);
                    var chatPath = Path.Combine(slot, "chat_region.png");
                    var reset = previousLayout is null || previousLayout.ScreenshotSize != pixels.ScreenshotSize || previousLayout.ChatRegion != pixels.ChatRegion;
                    var change = reset || previousChat is null ? null : detector.Detect(previousChat, chatPath);
                    var kind = previousChat is null ? "BaselineSnapshot" : reset ? "LayoutResetSnapshot" : change!.Changed ? "ChangedSnapshot" : "Unchanged";
                    string? eventDirectory = null; MessageSession? buyerSession = null;
                    var newCount = 0; var messageCount = 0;
                    if (reset || change!.Changed)
                    {
                        eventDirectory = Path.Combine(run, $"frame_{attempts:D6}"); Directory.CreateDirectory(eventDirectory);
                        File.Copy(fullPath, Path.Combine(eventDirectory, "window.png"));
                        foreach (var (file, _) in pixels.GetRegions()) File.Copy(Path.Combine(slot, file), Path.Combine(eventDirectory, file));
                        AtomicJson.Write(Path.Combine(eventDirectory, "layout.json"), layout);
                        var productId = "";
                        try
                        {
                            productCalls++;
                            var ocr = await Recognize(Path.Combine(eventDirectory, "product_region.png"));
                            AtomicJson.Write(Path.Combine(eventDirectory, "product-ocr.json"), ocr);
                            var product = profiles.Resolve(ocr); productId = product.ProductId ?? "";
                            AtomicJson.Write(Path.Combine(eventDirectory, "product-context.json"), product);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            recoveries++; SafeAppend(run, "recovery.jsonl", new { Frame = attempts, Stage = "ProductOcr", Error = ex.GetType().Name, ProductId = "" });
                        }
                        chatCalls++;
                        var chatOcr = await Recognize(Path.Combine(eventDirectory, "chat_region.png"));
                        var batch = structurer.Structure(chatOcr, productId, "SameCaptureVisibleProductNotVerifiedMessageAssociation");
                        messageCount = batch.Messages.Count; confirmedObservations += messageCount;
                        AtomicJson.Write(Path.Combine(eventDirectory, "chat-ocr.json"), chatOcr);
                        AtomicJson.Write(Path.Combine(eventDirectory, "buyer-messages.json"), batch);
                        var observedBuyer = SessionRegistry.ReadBuyer(batch, chatOcr);
                        if (observedBuyer is not null)
                        {
                            var frameCaptureId = $"{Path.GetFileName(run)}:frame_{attempts:D6}";
                            buyerSession = registry.Resolve(observedBuyer, productId, captureTime, frameCaptureId); sessionIds.Add(buyerSession.SessionId);
                            lifecycle.Observe(buyerSession, captureTime);
                            tracker = registry.Tracker(buyerSession, _displayNow()); comparison = tracker.Compare(batch.Messages, _displayNow());
                            AtomicJson.Write(Path.Combine(eventDirectory, "message-comparison.json"), comparison);
                            AtomicJson.Write(Path.Combine(eventDirectory, "buyer-session.json"), buyerSession);
                            foreach (var decision in comparison.Decisions)
                            {
                                var message = batch.Messages[decision.MessageIndex];
                                var source = new OcrMessageSource(_ => Task.FromResult(batch with { Messages = [message], Evidence = [] }), buyerSession.SellerId, buyerSession.BuyerId);
                                await foreach (var envelope in source.ReadAsync(token))
                                {
                                    var messageId = MessageFingerprint.StableId(buyerSession.SessionId, message);
                                    var captureId = frameCaptureId;
                                    var item = new DispatchItem(messageId, decision.Fingerprint, buyerSession, envelope, captureTime, options.AiShadow,
                                        CaptureId: captureId, AiRequestId: options.AiShadow ? MessageFingerprint.AiRequestId(messageId) : null);
                                    dispatch.Record(item, decision.Disposition, "NotStarted", "NotStarted", "Original source time preserved; normalized key only for outer comparison");
                                    if (decision.Disposition == "New")
                                    {
                                        if (agentTrace is not null && !agentTrace.MessageCaptured(item)) throw new IOException("AI capture/session audit is unavailable.");
                                        var context = SessionContextBuilder.Build(item, dispatch.Snapshot());
                                        AtomicJson.Write(Path.Combine(eventDirectory, $"session-context_{decision.MessageIndex + 1:D3}.json"), context);
                                        if (options.AiShadow && buyerSession.SessionId != allowedAiSession)
                                        {
                                            outOfScopeMessages++;
                                            agentTrace!.OutOfScope(item);
                                            dispatch.Record(item, "SessionOutOfScope", "Skipped", "Held", "AI test is bound to a different buyer session");
                                        }
                                        else
                                        {
                                            if (dispatch.Submit(item, options.AiShadow ? context : null)) { runIds.Add(item.MessageId); newCount++; }
                                            else duplicates++;
                                        }
                                        AtomicJson.Write(Path.Combine(eventDirectory, $"envelope_{decision.MessageIndex + 1:D3}.json"), envelope);
                                    }
                                }
                            }
                            baselines += comparison.Decisions.Count(d => d.Disposition == "Baseline");
                            duplicates += comparison.Decisions.Count(d => d.Disposition is "DuplicateFingerprint" or "ContextChangedOnly");
                            historical += comparison.Decisions.Count(d => d.Disposition == "HistoricalBeforeObservation");
                            if (comparison.IsBaseline) Console.WriteLine($"[Shadow] BASELINE READY: buyer session {buyerSession.SessionId[..12]}; no historical dispatch.");
                        }
                        else
                        {
                            if (messageCount > 0)
                                agentTrace.SessionMissing($"{Path.GetFileName(run)}:frame_{attempts:D6}", productId, messageCount);
                            var unknown = new MessageSession(MessageFingerprint.SessionId(run, "", "unknown"), "", "", "UnknownOrAmbiguous", "Unverified", productId, null);
                            foreach (var message in batch.Messages)
                            {
                                var envelope = new MessageEnvelope("Qianniu.OCR", "", "", productId, [new(message.Role, message.Text, message.Timestamp)]);
                                var held = new DispatchItem(MessageFingerprint.StableId(unknown.SessionId, message), MessageFingerprint.Create(message), unknown, envelope, captureTime, options.AiShadow);
                                dispatch.Record(held, "IdentityUnconfirmed", "NotStarted", "Held", "Cannot bind one reliable buyer label; raw OCR evidence retained");
                            }
                            SafeAppend(run, "recovery.jsonl", new { Frame = attempts, Stage = "IdentityUnconfirmed", ConfirmedMessages = messageCount, Action = "Hold evidence; no buyer/session guessing" });
                        }
                    }
                    frames++; consecutiveFailures = 0;
                    SafeAppend(run, "shadow.jsonl", new { Frame = attempts, CaptureTime = captureTime, Mode = "ShadowOnly", Kind = kind, NewMessages = newCount,
                        ConfirmedMessages = messageCount, SessionId = buyerSession?.SessionId, Buyer = buyerSession?.ObservedBuyer, ProductId = buyerSession?.ProductId ?? "",
                        Change = change, Decisions = comparison?.Decisions, EventDirectory = eventDirectory, AiEnabled = options.AiShadow, SendEnabled = false });
                    if (kind != "Unchanged") Console.WriteLine($"[Shadow] frame={attempts} {kind}; messages={messageCount}; new={newCount}; totalNew={runIds.Count}");
                    previousChat = chatPath; previousLayout = pixels;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures++; recoveries++; consecutiveFailures++;
                    if (tracker is not null && comparison is not null) tracker.ForgetUncommitted(comparison.NewMessages);
                    previousChat = null; previousLayout = null; window = null;
                    SafeAppend(run, "recovery.jsonl", new { Frame = attempts, At = DateTimeOffset.Now, Stage = "FrameFailed", Error = ex.GetType().Name, ex.Message, Action = "Rediscover/retry; durable dispatch and buyer dedup retained" });
                    Console.WriteLine($"[Recovery] frame={attempts}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { if (fullPath is not null && File.Exists(fullPath)) File.Delete(fullPath); }
                    catch (IOException) { recoveries++; }
                }
                if (watch.Elapsed >= nextHeartbeat)
                {
                    var now = DateTimeOffset.Now;
                    lifecycle.SweepInactive(now, TimeSpan.FromMinutes(30));
                    var sessionMetrics = lifecycle.Snapshot();
                    var currentItems = dispatch.Snapshot().Where(i => runIds.Contains(i.MessageId)).ToArray();
                    using var process = Process.GetCurrentProcess();
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    long runBytes = -1, stateBytes = -1;
                    try { runBytes = RunLogWriter.SizeBytes(run); stateBytes = RunLogWriter.SizeBytes(stateRoot); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        recoveries++;
                        SafeAppend(run, "recovery.jsonl", new { At = now, Stage = "StorageMeasurementFailed", Error = ex.GetType().Name });
                    }
                    var aiCalls = currentItems.Sum(i => i.AiCalls);
                    var previews = currentItems.Count(i => i.AiEnabled && i.State == "Completed");
                    var errors = failures + recoveries + dispatch.JournalErrors + agentTrace.Errors + dispatch.KnowledgeErrors
                        + currentItems.Count(i => i.State is "AiFailed" or "Uncertain" or "DecisionFailed");
                    var persistedSessionsNow = Directory.Exists(Path.Combine(stateRoot, "sessions"))
                        ? Directory.EnumerateDirectories(Path.Combine(stateRoot, "sessions")).Count() : 0;
                    SafeAppend(run, "health.jsonl", new { At = now, StartedAt = startedAt, ElapsedSeconds = watch.Elapsed.TotalSeconds,
                        Attempts = attempts, SuccessfulFrames = frames, FailedFrames = failures,
                        ConfirmedObservations = confirmedObservations, CapturedMessages = runIds.Count,
                        BuyerSessions = sessionMetrics.Observed, PersistedSessions = persistedSessionsNow,
                        sessionMetrics.Active, sessionMetrics.Inactive, sessionMetrics.Closed, sessionMetrics.Reopened,
                        ProductReferences = sessionMetrics.ProductReferences, AiCalls = aiCalls, ReplyPreviews = previews,
                        KnowledgeErrors = dispatch.KnowledgeErrors,
                        ErrorCount = errors, Pending = currentItems.Count(i => i.State is "Pending" or "Dispatching"),
                        Uncertain = currentItems.Count(i => i.State == "Uncertain"),
                        WorkingSetBytes = process.WorkingSet64, PeakWorkingSetBytes = peakWorkingSet,
                        RunBytes = runBytes, StateBytes = stateBytes, AiEnabled = options.AiShadow,
                        SendEnabled = false, SentMessages = 0 });
                    Console.WriteLine($"[Health] elapsed={watch.Elapsed.TotalMinutes:F1}m frames={frames} new={runIds.Count} sessions={sessionMetrics.Observed} refs={sessionMetrics.ProductReferences} AI={aiCalls} previews={previews} errors={errors}");
                    if (sessionMetrics.Observed > 256)
                    {
                        status = "SessionGrowthLimitReached";
                        SafeAppend(run, "recovery.jsonl", new { At = now, Stage = "SessionGrowthLimitReached", Observed = sessionMetrics.Observed, Action = "Stop without deleting session evidence" });
                        break;
                    }
                    if (runBytes >= options.MaxRunMegabytes * 1024 * 1024 || stateBytes >= options.MaxStateMegabytes * 1024 * 1024)
                    {
                        status = "StorageLimitReached";
                        SafeAppend(run, "recovery.jsonl", new { At = now, Stage = "StorageLimitReached", RunBytes = runBytes,
                            StateBytes = stateBytes, options.MaxRunMegabytes, options.MaxStateMegabytes, Action = "Stop without deleting audit evidence" });
                        break;
                    }
                    nextHeartbeat = watch.Elapsed.Add(TimeSpan.FromMinutes(1));
                }
                if (options.MaximumFrames is null || attempts < options.MaximumFrames)
                    await Task.Delay(consecutiveFailures == 0 ? options.IntervalMilliseconds : Math.Min(10000, 500 * (1 << Math.Min(consecutiveFailures, 4))), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "Cancelled"; }
        finally
        {
            try { await dispatch.StopAsync(); }
            finally { lifecycle.CloseAll(DateTimeOffset.Now); }
        }
        var current = dispatch.Snapshot().Where(i => runIds.Contains(i.MessageId)).ToArray();
        var completed = current.Count(i => i.State == "Completed"); var deferred = current.Length - completed;
        if (status is "Completed" or "StoppedByRequest" && (failures > 0 || deferred > 0)) status = "CompletedWithIssues";
        var endedAt = DateTimeOffset.Now;
        var sessionStats = lifecycle.Snapshot();
        using (var process = Process.GetCurrentProcess()) peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        var runSize = RunLogWriter.SizeBytes(run);
        var stateSize = RunLogWriter.SizeBytes(stateRoot);
        var archives = Path.Combine(run, "log-archive");
        var archiveSegments = Directory.Exists(archives) ? Directory.EnumerateFiles(archives, "*.jsonl").Count() : 0;
        var persistedSessions = Directory.Exists(Path.Combine(stateRoot, "sessions"))
            ? Directory.EnumerateDirectories(Path.Combine(stateRoot, "sessions")).Count() : 0;
        var errorCount = failures + recoveries + dispatch.JournalErrors + agentTrace.Errors + dispatch.KnowledgeErrors
            + current.Count(i => i.State is "AiFailed" or "Uncertain" or "DecisionFailed");
        var summary = new ShadowRunSummary(status, attempts, frames, failures, productCalls, chatCalls, runIds.Count, completed, deferred, run,
            runIds.Count, baselines, duplicates, historical, completed, options.AiShadow, current.Count(i => i.AiInvoked), options.AiShadow ? completed : 0,
            watch.Elapsed.TotalSeconds, sessionIds.Count, current.Count(i => i.State == "Uncertain"), recoveries, dispatch.JournalErrors, stateRoot,
            outOfScopeMessages, current.Count(i => i.State == "EmptyReply"), current.Count(i => i.State == "AiFailed"),
            current.Sum(i => i.AiCalls), StartedAt: startedAt, EndedAt: endedAt, PeakWorkingSetBytes: peakWorkingSet,
            ProductReferences: sessionStats.ProductReferences, ActiveSessions: sessionStats.Active,
            InactiveSessions: sessionStats.Inactive, ClosedSessions: sessionStats.Closed,
            ReopenedSessions: sessionStats.Reopened, ConfirmedObservations: confirmedObservations,
            ErrorCount: errorCount, RunBytes: runSize, StateBytes: stateSize,
            LogArchiveSegments: archiveSegments, PersistedSessions: persistedSessions,
            KnowledgeErrors: dispatch.KnowledgeErrors, ProductKnowledgeEnabled: options.ProductKnowledgePath is not null);
        AtomicJson.Write(Path.Combine(run, "summary.json"), summary);
        Console.WriteLine($"[Shadow] stopped: {JsonSerializer.Serialize(summary)}");
        return summary;

        Task<OcrResult> Recognize(string path) => _ocr is null ? native.RecognizeAsync(path, token) : Task.FromResult(_ocr(path));
    }
    private static void SafeAppend<T>(string directory, string file, T value)
    {
        try { RunLogWriter.Append(directory, file, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"[Recovery] diagnostic log unavailable: {file}: {ex.GetType().Name}"); }
    }
}
