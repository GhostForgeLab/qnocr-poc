using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

/// <summary>Read-only, per-run assistant dashboard. Counts are scoped by CaptureId, never by a shared state's age.</summary>
public sealed class AssistantRuntimeDashboard(string stateRoot, string runDirectory)
{
    private readonly string _stateRoot = Path.GetFullPath(stateRoot);
    private readonly string _run = Path.GetFullPath(runDirectory);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string RunKey => Path.GetFileName(_run);
    private int _readErrors;

    public AssistantRuntimeStats Snapshot()
    {
        var metadata = ReadRunMetadata();
        var (items, previews, feedback, states) = ReadState();
        var ids = previews.Select(p => p.ReplyPreviewId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var audit = ReadAudit().Where(e => ids.Contains(e.ReplyPreviewId)).ToArray();
        var observations = ObserveSessions(previews, feedback);
        var counts = feedback.Select(r => r.Feedback.FeedbackType).ToArray();
        var accepted = counts.Count(t => t == ReplyFeedbackTypes.Accepted);
        var edited = counts.Count(t => t == ReplyFeedbackTypes.Edited);
        var rejected = counts.Count(t => t == ReplyFeedbackTypes.Rejected);
        var total = accepted + edited + rejected;
        var sessions = Math.Max(items.Select(i => i.Session.SessionId).Distinct(StringComparer.Ordinal).Count(), metadata.BuyerSessions);
        var withProduct = previews.Count(p => !string.IsNullOrWhiteSpace(p.Input.ProductId));
        var editDelays = states.Where(s => s.EditedAt is not null)
            .Select(s => Math.Max(0, (s.EditedAt!.Value - s.CreatedAt).TotalSeconds)).ToArray();
        var unverified = previews.Count(p => !string.IsNullOrWhiteSpace(p.Input.ProductId) &&
            p.Input.MessageProductLinks?.Any(link => link.MessageId == p.Input.MessageId &&
                link.ProductId == p.Input.ProductId && link.AssociationStatus == "Verified") != true);
        return new AssistantRuntimeStats(DateTimeOffset.Now, _stateRoot, _run, metadata.StartedAt,
            metadata.ElapsedSeconds, metadata.Status, metadata.Frames, items.Count, sessions,
            previews.Count, audit.Count(e => e.Event == "HUMAN_EDITED"),
            states.Count(s => s.EditedAt is not null), audit.Count(e => e.Event == "COPY_ACTION"),
            rejected, total, accepted, edited, rejected,
            Rate(accepted, total), Rate(edited, total), Rate(rejected, total),
            observations.Count(s => s.PreviewCount >= 2), observations.Sum(s => s.ContextContinuousPairs),
            observations.Sum(s => s.ContextUnprovenPairs), metadata.AiCalls, metadata.SentMessages,
            metadata.SafetySource, _readErrors, withProduct, previews.Count - withProduct, unverified,
            feedback.Count(f => f.Feedback.ProductReferenceAssessment == "Correct"),
            feedback.Count(f => f.Feedback.ProductReferenceAssessment == "Incorrect"),
            feedback.Count(f => f.Feedback.ProductReferenceAssessment == "Unknown"),
            feedback.Count(f => f.Feedback.ContextAssessment == "Continued"),
            feedback.Count(f => f.Feedback.ContextAssessment == "Lost"),
            feedback.Count(f => f.Feedback.ContextAssessment == "Unknown"),
            editDelays.Length == 0 ? null : editDelays.Average());
    }

    public IReadOnlyList<ConversationSample> Samples()
    {
        ReadRunMetadata();
        var (_, previews, feedback, _) = ReadState();
        var recorded = feedback.ToDictionary(f => f.Feedback.ReplyPreviewId, f => f.Sample, StringComparer.OrdinalIgnoreCase);
        return previews.Select(preview => recorded.TryGetValue(preview.ReplyPreviewId, out var sample)
            ? sample : new ConversationSample(preview.ReplyPreviewId, preview.SessionId, preview.CaptureId,
                AssistantSampleRedactor.Redact(preview.Input.Messages.LastOrDefault(m => m.Role == "buyer")?.Text ?? ""),
                AssistantSampleRedactor.Redact(preview.Reply), "", "Unreviewed", preview.Input.ProductId,
                (preview.Input.ProductReferences ?? Array.Empty<ProductReference>()).Select(r => r.ProductId)
                    .Distinct(StringComparer.Ordinal).ToArray(), preview.Input.Messages.Count,
                preview.Input.CapturedAt, default))
            .OrderBy(s => s.CapturedAt).ToArray();
    }
    public IReadOnlyList<SessionObservation> Sessions()
    {
        ReadRunMetadata();
        var (_, previews, feedback, _) = ReadState();
        return ObserveSessions(previews, feedback);
    }

    private (List<DispatchItem> Items, List<ReplyPreview> Previews,
        List<AssistantFeedbackRecord> Feedback, List<AssistedReplyState> States) ReadState()
    {
        var items = new List<DispatchItem>(); var previews = new List<ReplyPreview>();
        var feedback = new List<AssistantFeedbackRecord>(); var states = new List<AssistedReplyState>();
        var outbox = Path.Combine(_stateRoot, "outbox");
        if (!Directory.Exists(outbox)) return (items, previews, feedback, states);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(outbox))
        {
            try
            {
                var itemPath = Path.Combine(folder, "item.json");
                if (File.Exists(itemPath))
                {
                    var item = JsonSerializer.Deserialize<DispatchItem>(File.ReadAllText(itemPath), Json);
                    if (item?.CaptureId?.StartsWith(RunKey + ":", StringComparison.Ordinal) == true) items.Add(item);
                }
                var previewPath = Path.Combine(folder, "reply-preview.json");
                if (!File.Exists(previewPath)) continue;
                var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(previewPath), Json);
                if (preview?.CaptureId?.StartsWith(RunKey + ":", StringComparison.Ordinal) != true) continue;
                if (!seen.Add(preview.ReplyPreviewId)) { _readErrors++; continue; }
                previews.Add(preview);
                var statePath = Path.Combine(folder, "assistant-reply.json");
                if (File.Exists(statePath))
                {
                    var state = JsonSerializer.Deserialize<AssistedReplyState>(File.ReadAllText(statePath), Json);
                    if (state?.ReplyPreviewId == preview.ReplyPreviewId) states.Add(state);
                    else _readErrors++;
                }
                var feedbackPath = Path.Combine(folder, "assistant-feedback.json");
                if (File.Exists(feedbackPath))
                {
                    var record = JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(feedbackPath), Json);
                    if (record?.Feedback.ReplyPreviewId == preview.ReplyPreviewId && record.Sample.ReplyPreviewId == preview.ReplyPreviewId)
                        feedback.Add(record);
                    else _readErrors++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _readErrors++;
                Console.Error.WriteLine($"Assistant dashboard held unreadable state: {Path.GetFileName(folder)} ({ex.GetType().Name})");
            }
        }
        return (items, previews, feedback, states);
    }

    private IReadOnlyList<AssistantAuditEvent> ReadAudit()
    {
        var files = new List<string>();
        var active = Path.Combine(_stateRoot, "assistant-actions.jsonl");
        if (File.Exists(active)) files.Add(active);
        var archive = Path.Combine(_stateRoot, "log-archive");
        if (Directory.Exists(archive)) files.AddRange(Directory.EnumerateFiles(archive, "assistant-actions.*.jsonl"));
        var events = new List<AssistantAuditEvent>();
        foreach (var file in files)
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<AssistantAuditEvent>(line, Json);
                        if (entry is not null) events.Add(entry);
                        else _readErrors++;
                    }
                    catch (JsonException) { _readErrors++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readErrors++;
                Console.Error.WriteLine($"Assistant audit held unreadable: {Path.GetFileName(file)} ({ex.GetType().Name})");
            }
        }
        return events;
    }

    private IReadOnlyList<SessionObservation> ObserveSessions(IReadOnlyList<ReplyPreview> previews,
        IReadOnlyList<AssistantFeedbackRecord> feedback)
    {
        var results = new List<SessionObservation>();
        foreach (var group in previews.GroupBy(p => p.SessionId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(p => p.Input.CapturedAt).ThenBy(p => p.CreatedAt).ToArray();
            var continuous = 0; var unproven = 0;
            for (var i = 1; i < ordered.Length; i++)
            {
                var previous = ordered[i - 1].Input.Messages.LastOrDefault(m => m.Role == "buyer");
                var current = ordered[i].Input.Messages;
                if (previous is not null && current.Any(m => m.Role == previous.Role && m.Text == previous.Text
                    && m.Timestamp == previous.Timestamp) && current.Count > ordered[i - 1].Input.Messages.Count)
                    continuous++;
                else unproven++;
            }
            results.Add(new SessionObservation(group.Key, ordered.Length,
                ordered.Select(p => p.Input.Messages.LastOrDefault(m => m.Role == "buyer")?.Text)
                    .Where(t => t is not null).Distinct(StringComparer.Ordinal).Count(),
                feedback.Count(f => f.Feedback.SessionId == group.Key), continuous, unproven,
                ordered.Select(p => p.Input.ProductId).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal).ToArray(), ordered.Min(p => p.CreatedAt), ordered.Max(p => p.CreatedAt)));
        }
        return results.OrderByDescending(s => s.LastPreviewAt).ToArray();
    }

    private (DateTimeOffset StartedAt, double ElapsedSeconds, string Status, int Frames, int BuyerSessions,
        int AiCalls, int? SentMessages, string SafetySource) ReadRunMetadata()
    {
        var sessionPath = Path.Combine(_run, "session.json");
        if (!File.Exists(sessionPath)) throw new FileNotFoundException("Shadow run session.json is required.", sessionPath);
        using var session = JsonDocument.Parse(File.ReadAllText(sessionPath));
        var configuredRoot = Path.GetFullPath(session.RootElement.GetProperty("StateDirectory").GetString() ?? "");
        if (!string.Equals(configuredRoot, _stateRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Run directory belongs to a different state root.");
        if (session.RootElement.TryGetProperty("SendEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Assistant dashboard only accepts Send OFF Shadow runs.");
        var started = session.RootElement.GetProperty("StartedAt").GetDateTimeOffset();
        var summaryPath = Path.Combine(_run, "summary.json");
        if (File.Exists(summaryPath))
        {
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var root = summary.RootElement;
            var sent = GetInt(root, "SentMessages");
            return (started, GetDouble(root, "ElapsedSeconds") ?? 0, GetString(root, "Status") ?? "Completed",
                GetInt(root, "SuccessfulFrames") ?? 0, GetInt(root, "BuyerSessions") ?? 0,
                GetInt(root, "AiCalls") ?? 0, sent, "ShadowSummary");
        }
        var healthPath = Path.Combine(_run, "health.jsonl");
        if (File.Exists(healthPath))
        {
            var last = File.ReadLines(healthPath).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (last is not null)
            {
                using var health = JsonDocument.Parse(last);
                var root = health.RootElement;
                return (started, GetDouble(root, "ElapsedSeconds") ?? (DateTimeOffset.Now - started).TotalSeconds,
                    "Running", GetInt(root, "SuccessfulFrames") ?? 0, GetInt(root, "BuyerSessions") ?? 0,
                    GetInt(root, "AiCalls") ?? 0, GetInt(root, "SentMessages"), "ShadowHealth");
            }
        }
        return (started, Math.Max(0, (DateTimeOffset.Now - started).TotalSeconds), "Starting", 0, 0, 0, null, "Unavailable");
    }

    private static string? GetString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? GetInt(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    private static double? GetDouble(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static double? Rate(int numerator, int denominator) => denominator == 0 ? null : (double)numerator / denominator;
}
