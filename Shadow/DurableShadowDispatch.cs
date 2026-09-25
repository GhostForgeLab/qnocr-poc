using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed record DispatchItem(string MessageId, string Fingerprint, MessageSession Session, MessageEnvelope Envelope,
    DateTimeOffset CaptureTime, bool AiEnabled, string State = "Pending", int Attempts = 0,
    DateTimeOffset NextAttempt = default, string? ReceiptPath = null, bool AiInvoked = false);

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
    private Task? _worker;
    private volatile bool _stopping;
    public int JournalErrors { get; private set; }
    public int CorruptStateFiles { get; private set; }

    public DurableShadowDispatch(string root, string journal, CancellationToken token,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>> send, bool? aiMode = null)
    {
        _root = Path.GetFullPath(root); _journal = journal; _token = token; _send = send; _aiMode = aiMode;
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
    public bool Submit(DispatchItem item)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(item.MessageId) || File.Exists(Path.Combine(Folder(item), "item.json"))) { Record(item, "OuterDuplicate", "Skipped", "NotRepeated", "Existing durable MessageId, including unreadable state held for inspection"); return false; }
            if (_items.Values.Count(i => i.State is "Pending" or "Dispatching") >= 1000) throw new IOException("Outbox capacity reached; retain capture evidence and apply backpressure.");
            var directory = Folder(item);
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
            try { Directory.CreateDirectory(Path.GetDirectoryName(_journal)!); File.AppendAllText(_journal, JsonSerializer.Serialize(entry) + Environment.NewLine); }
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
                try
                {
                    result = await _send(Path.Combine(Folder(item), "envelope.json"), item.Session.IdentityPath,
                        Path.Combine(Folder(item), $"attempt_{item.Attempts:D4}"), _token);
                }
                catch (Exception ex)
                {
                    item = item with { State = "Uncertain" }; Save(item);
                    Record(item, "Uncertain", ex.GetType().Name, "UnconfirmedHeld", "Exception cannot prove target did not accept; no automatic replay");
                    continue;
                }
                var retry = result.Status is "HostUnavailable" or "HostNotStarted" or "RetryableFailure";
                var state = result.Status == "Converted" ? "Completed" : retry ? "Pending" : result.Status == "Rejected" ? "Rejected" : "Uncertain";
                item = item with { State = state, ReceiptPath = result.ReceiptPath, AiInvoked = result.AiInvoked,
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
