using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Decision;
using QnocrPoc.Knowledge;

namespace QnocrPoc.Shadow;

public sealed record DispatchItem(string MessageId, string Fingerprint, MessageSession Session, MessageEnvelope Envelope,
    DateTimeOffset CaptureTime, bool AiEnabled, string State = "Pending", int Attempts = 0,
    DateTimeOffset NextAttempt = default, string? ReceiptPath = null, bool AiInvoked = false,
    string? CaptureId = null, string? AiRequestId = null, string? AiResultStatus = null, int AiCalls = 0);

/// <summary>Local outer idempotency and recoverable outbox. Ambiguous target outcomes are held, never blindly replayed.</summary>
public sealed class DurableShadowDispatch : IDisposable
{
    private readonly string _root;
    private readonly string _journal;
    private readonly FileStream _lock;
    private readonly Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>> _send;
    private readonly object _gate = new();
    private readonly Dictionary<string, DispatchItem> _items = new(StringComparer.Ordinal);
    private readonly CancellationToken _token;
    private readonly bool? _aiMode;
    private readonly AgentPipelineTrace? _agentTrace;
    private readonly IIntentClassifier _intentClassifier;
    private readonly IKnowledgeProvider _knowledgeProvider;
    private Task? _worker;
    private volatile bool _stopping;
    public int JournalErrors { get; private set; }
    public int CorruptStateFiles { get; private set; }
    public int KnowledgeErrors { get; private set; }

    public DurableShadowDispatch(string root, string journal, CancellationToken token,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>> send, bool? aiMode = null,
        AgentPipelineTrace? agentTrace = null, IIntentClassifier? intentClassifier = null,
        IKnowledgeProvider? knowledgeProvider = null)
    {
        _root = Path.GetFullPath(root); _journal = journal; _token = token; _send = send; _aiMode = aiMode;
        _agentTrace = agentTrace; _intentClassifier = intentClassifier ?? new RuleBasedIntentClassifier();
        _knowledgeProvider = knowledgeProvider ?? new EmptyKnowledgeProvider();
        Directory.CreateDirectory(_root);
        _lock = new FileStream(Path.Combine(_root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var file in Directory.EnumerateFiles(_root, "item.json", SearchOption.AllDirectories))
        {
            try
            {
                var item = JsonSerializer.Deserialize<DispatchItem>(File.ReadAllText(file))!;
                if (item.MessageId.Length != 64 || !item.MessageId.All(Uri.IsHexDigit) || Path.GetFileName(Path.GetDirectoryName(file)) != item.MessageId)
                    throw new InvalidDataException("Invalid durable message identity.");
                if (item.State == "Dispatching") { item = item with { State = "Uncertain" }; AtomicJson.Write(file, item); }
                if (item.State == "ConfigurationHold" && item.AiEnabled == _aiMode) { item = item with { State = "Pending" }; AtomicJson.Write(file, item); }
                _items[item.MessageId] = item;
                if (_items.Count > 11000)
                    foreach (var old in _items.Values.Where(i => i.State == "Completed").OrderBy(i => i.CaptureTime).Take(1000).ToArray()) _items.Remove(old.MessageId);
            }
            catch (Exception ex) { CorruptStateFiles++; Console.WriteLine($"[Recovery] state file held unreadable: {file}; {ex.GetType().Name}"); }
        }
    }
    public void Start() => _worker = Task.Run(WorkAsync);
    public IReadOnlyList<DispatchItem> Snapshot() { lock (_gate) return _items.Values.ToArray(); }
    public bool Submit(DispatchItem item, SessionContextView? context = null)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(item.MessageId) || File.Exists(Path.Combine(Folder(item), "item.json"))) { Record(item, "OuterDuplicate", "Skipped", "NotRepeated", "Existing durable MessageId, including unreadable state held for inspection"); return false; }
            if (_items.Values.Count(i => i.State is "Pending" or "Dispatching") >= 1000) throw new IOException("Outbox capacity reached; retain capture evidence and apply backpressure.");
            var directory = Folder(item);
            if (item.AiEnabled)
            {
                if (context is null || context.SessionId != item.Session.SessionId || context.MessageId != item.MessageId)
                    throw new InvalidDataException("AI request requires a matching durable session context.");
                AtomicJson.Write(Path.Combine(directory, "context.json"), context);
            }
            AtomicJson.Write(Path.Combine(directory, "envelope.json"), item.Envelope);
            AtomicJson.Write(Path.Combine(directory, "item.json"), item);
            _items[item.MessageId] = item;
            Record(item, "Persisted", "Pending", "NotStarted", null);
            return true;
        }
    }
    public void Record(DispatchItem item, string stage, string adapter, string dispatch, string? detail)
    {
        var entry = new MessageLifecycle(item.CaptureTime, item.Envelope.Source, item.Session.ObservedBuyer, item.Envelope.ProductId,
            item.Session.SessionId, item.MessageId, item.Fingerprint, stage, adapter, dispatch,
            item.AiEnabled ? item.AiInvoked ? "Invoked" : "EnabledNotConfirmed" : "OFF", item.Attempts, detail);
        lock (_gate)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(_journal)!); RunLogWriter.Append(Path.GetDirectoryName(_journal)!, Path.GetFileName(_journal), entry); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { JournalErrors++; Console.WriteLine($"[Recovery] lifecycle log unavailable; durable state remains authoritative: {item.MessageId} {stage}"); }
        }
    }
    private string Folder(DispatchItem item) => Path.Combine(_root, "outbox", item.MessageId);
    private void Save(DispatchItem item)
    {
        lock (_gate)
        {
            AtomicJson.Write(Path.Combine(Folder(item), "item.json"), item); _items[item.MessageId] = item;
            if (_items.Count > 11000)
                foreach (var old in _items.Values.Where(i => i.State == "Completed").OrderBy(i => i.CaptureTime).Take(1000).ToArray()) _items.Remove(old.MessageId);
        }
    }
    private async Task WorkAsync()
    {
        while (!_stopping && !_token.IsCancellationRequested)
        {
            try
            {
                DispatchItem? item;
                lock (_gate) item = _items.Values.Where(i => i.State == "Pending" && i.NextAttempt <= DateTimeOffset.UtcNow)
                    .OrderBy(i => i.CaptureTime).FirstOrDefault();
                if (item is null) { await Task.Delay(300, _token); continue; }
                if (_aiMode is { } mode && item.AiEnabled != mode)
                {
                    item = item with { State = "ConfigurationHold" }; Save(item); Record(item, "ConfigurationHold", "NotStarted", "Held", "Pending event AI mode differs from current explicitly selected mode"); continue;
                }
                if (item.Session.IdentityPath is null)
                {
                    item = item with { State = "Rejected" }; Save(item); Record(item, "Rejected", "IdentityUnknown", "NotStarted", "No verified/operator test mapping"); continue;
                }
                item = item with { State = "Dispatching", Attempts = item.Attempts + 1 };
                Save(item); Record(item, "Attempt", "Running", "Unconfirmed", null);
                ShadowAdapterResult result;
                SessionContextView? context = null;
                IntentResult? decision = null;
                var knowledgeStatus = KnowledgeStatuses.NotRequired;
                DateTimeOffset requestedAt = default;
                try
                {
                    if (item.AiEnabled)
                        context = JsonSerializer.Deserialize<SessionContextView>(File.ReadAllText(Path.Combine(Folder(item), "context.json")))
                            ?? throw new InvalidDataException("AI context is unavailable.");
                    if (context is not null)
                    {
                        try
                        {
                            decision = _intentClassifier.Classify(context);
                            if (decision is null || string.IsNullOrWhiteSpace(decision.IntentName) ||
                                decision.IntentName.Length > 64 || !decision.IntentName.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ||
                                decision.RiskLevel is not ("Low" or "Medium" or "High") ||
                                decision.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
                                throw new InvalidDataException("Intent classifier returned invalid decision metadata.");
                            AtomicJson.Write(Path.Combine(Folder(item), "decision.json"), decision);
                            if (_agentTrace is not null && !_agentTrace.DecisionCreated(item, context, decision))
                                throw new IOException("Decision audit failed; AI request not started.");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            item = item with { State = "DecisionFailed" }; Save(item);
                            Record(item, "DecisionFailed", ex.GetType().Name, "NoAiCall", "Decision failed before AI request; no automatic retry");
                            _agentTrace?.DecisionError(item, context, ex.GetType().Name + "; AI request not started");
                            continue;
                        }
                    }
                    if (context is not null && decision is not null)
                    {
                        if (decision.NeedKnowledge)
                        {
                            var request = KnowledgeRequestBuilder.Build(context, decision);
                            _agentTrace?.KnowledgeRequested(item, context, request);
                            KnowledgeResult knowledge;
                            try
                            {
                                knowledge = await _knowledgeProvider.GetAsync(request, _token);
                                if (knowledge is null || string.IsNullOrWhiteSpace(knowledge.Source) ||
                                    knowledge.MissingFields is null ||
                                    knowledge.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1) ||
                                    knowledge.Success && string.IsNullOrWhiteSpace(knowledge.Content))
                                    throw new InvalidDataException("Knowledge provider returned invalid metadata.");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                KnowledgeErrors++;
                                _agentTrace?.KnowledgeError(item, context, request, ex.GetType().Name);
                                knowledge = new KnowledgeResult(false, "None", null, "", request.NeedFields);
                            }
                            knowledgeStatus = knowledge.Success && knowledge.MissingFields.Count == 0
                                ? KnowledgeStatuses.Available : KnowledgeStatuses.Missing;
                            try
                            {
                                AtomicJson.Write(Path.Combine(Folder(item), "knowledge-result.json"), new
                                { knowledge.Success, knowledge.Source, knowledge.Confidence, knowledge.MissingFields, Status = knowledgeStatus });
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                KnowledgeErrors++;
                                _agentTrace?.KnowledgeError(item, context, request, ex.GetType().Name + "; metadata persistence failed");
                            }
                            _agentTrace?.KnowledgeResolved(item, context, request, knowledge);
                        }
                        else _agentTrace?.KnowledgeSkipped(item, context, decision);
                    }
                    requestedAt = DateTimeOffset.Now;
                    result = await _send(Path.Combine(Folder(item), "envelope.json"), item.Session.IdentityPath,
                        Path.Combine(Folder(item), $"attempt_{item.Attempts:D4}"), _token);
                }
                catch (Exception ex)
                {
                    item = item with { State = "Uncertain" }; Save(item);
                    Record(item, "Uncertain", ex.GetType().Name, "UnconfirmedHeld", "Exception cannot prove target did not accept; no automatic replay");
                    if (item.AiEnabled && _agentTrace is not null)
                        _agentTrace.Write(new(DateTimeOffset.Now, "AI_ERROR", item.CaptureId ?? "", item.Session.SessionId,
                            item.Session.BuyerId, item.MessageId, item.AiRequestId, null, item.Envelope.ProductId,
                            context?.Messages.Count ?? 1, null, null, ex.GetType().Name + "; request outcome uncertain"));
                    continue;
                }
                if (item.AiEnabled && context is not null && _agentTrace is not null)
                {
                    var inputLength = string.Join("\n", context.Messages.TakeLast(context.ModelInputMessageCount).Select(m => m.Text)).Length;
                    var length = result.PromptLength ?? inputLength;
                    var scope = result.PromptLength is null ? "PreCoordinatorQuestionText" : "QnbotModelQuestionArgument";
                    _agentTrace.Request(item, context, length, scope, result.AiRequestAt ?? requestedAt);
                    if (result.Status == "Converted" && string.IsNullOrWhiteSpace(result.ReplySuggestion))
                        result = result with { Status = "EmptyReply", Reason = "AI result was empty; no preview created." };
                    var aiResponseId = result.Status == "Converted" ? Guid.NewGuid().ToString("N") : null;
                    _agentTrace.Response(item, context, result, aiResponseId);
                    if (result.Status == "Converted")
                    {
                        var preview = new ReplyPreview(item.Session.SessionId, item.Session.BuyerId, context, result.ReplySuggestion!,
                            null, DateTimeOffset.Now, "Shadow", item.CaptureId ?? "", context.AiRequestId, aiResponseId!, Guid.NewGuid().ToString("N"), decision, knowledgeStatus);
                        try
                        {
                            AtomicJson.Write(Path.Combine(Folder(item), "reply-preview.json"), preview);
                            new PreviewConfirmationService(_root).InitializePreview(Folder(item), preview);
                            new AssistantReplyService(_root).InitializePreview(Folder(item), preview);
                            if (!_agentTrace.Preview(item, preview)) throw new IOException("Reply preview audit failed.");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            item = item with { State = "Uncertain", AiInvoked = result.AiInvoked, ReceiptPath = result.ReceiptPath };
                            Save(item); Record(item, "Uncertain", "PreviewAuditFailed", "Held", ex.Message);
                            _agentTrace.Write(new(DateTimeOffset.Now, "AI_ERROR", item.CaptureId ?? "", item.Session.SessionId,
                                item.Session.BuyerId, item.MessageId, context.AiRequestId, null, item.Envelope.ProductId,
                                context.Messages.Count, null, null, "Reply preview could not be audited."));
                            continue;
                        }
                    }
                }
                var retry = result.Status is "HostUnavailable" or "HostNotStarted" or "RetryableFailure";
                var state = result.Status == "Converted" ? "Completed" : retry ? "Pending" : result.Status is "Rejected" or "EmptyReply" or "AiFailed" ? result.Status : "Uncertain";
                item = item with { State = state, ReceiptPath = result.ReceiptPath, AiInvoked = result.AiInvoked, AiResultStatus = result.Status,
                    AiCalls = result.AiCalls,
                    NextAttempt = retry ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(item.Attempts, 6)))) : default };
                Save(item);
                Record(item, state, result.Status, state == "Completed" ? "AcceptedByShadowCoordinator" : retry ? "RetryScheduled" : "Held", result.Reason);
                Console.WriteLine($"[Dispatch] {item.MessageId[..12]} {state}; attempt={item.Attempts}; AI={(item.AiEnabled ? result.AiInvoked : false)}; Send=OFF");
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recovery] dispatch worker remains alive: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(1000, _token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public async Task StopAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!_token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline && Snapshot().Any(i => i.State is "Pending" or "Dispatching")) await Task.Delay(100);
        _stopping = true;
        if (_worker is not null) await _worker;
    }
    public void Dispose() => _lock.Dispose();
}
