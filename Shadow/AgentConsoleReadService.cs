using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

/// <summary>Read-only per-run projection over existing Shadow state; no new capture or AI call.</summary>
public sealed class AgentConsoleReadService
{
    private readonly string _state;
    private readonly string _run;
    private readonly string _runName;
    private readonly AssistantReplyService _assistant;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AgentConsoleReadService(string stateDirectory, string runDirectory)
    {
        _state = Path.GetFullPath(stateDirectory);
        _run = Path.GetFullPath(runDirectory);
        _runName = Path.GetFileName(_run);
        var metadata = File.Exists(Path.Combine(_run, "summary.json"))
            ? Path.Combine(_run, "summary.json") : Path.Combine(_run, "session.json");
        using var document = JsonDocument.Parse(File.ReadAllText(metadata));
        var root = document.RootElement;
        var paired = root.GetProperty("StateDirectory").GetString();
        if (paired is null || !string.Equals(Path.GetFullPath(paired), _state,
            StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("SendEnabled").ValueKind != JsonValueKind.False ||
            root.TryGetProperty("SentMessages", out var sent) && sent.GetInt32() != 0)
            throw new InvalidDataException("Agent Console requires its own Send-OFF Shadow run/state pair.");
        _assistant = new AssistantReplyService(_state);
    }

    public IReadOnlyList<AgentConsoleSessionRow> ListSessions()
    {
        var previews = Previews();
        return previews.GroupBy(preview => preview.SessionId, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.OrderByDescending(preview => preview.CreatedAt).First();
                var session = ReadSession(group.Key);
                return new AgentConsoleSessionRow(group.Key,
                    session.BuyerDisplayName ?? session.ObservedBuyer,
                    latest.Input.Messages.LastOrDefault()?.Text ?? "",
                    latest.CreatedAt > session.LastActivityAt ? latest.CreatedAt : session.LastActivityAt,
                    group.Count());
            }).OrderByDescending(row => row.UpdatedAt).ToArray();
    }

    public AgentConsoleConversation GetConversation(string sessionId)
    {
        var session = ReadSession(sessionId);
        var previews = Previews().Where(preview => preview.SessionId == sessionId)
            .OrderBy(preview => preview.CreatedAt).ToArray();
        var provider = ProviderEvents();
        var timeline = new List<AgentConsoleTimelineMessage>();
        var views = new List<AgentConsolePreviewRow>();
        foreach (var preview in previews)
        {
            var folder = Path.Combine(_state, "outbox", preview.Input.MessageId);
            var envelope = JsonSerializer.Deserialize<MessageEnvelope>(
                File.ReadAllText(Path.Combine(folder, "envelope.json")), Json)
                ?? throw new InvalidDataException("Shadow message envelope is unreadable.");
            if (envelope.Messages.Count != 1 || envelope.SellerId != session.SellerId ||
                envelope.BuyerId != preview.BuyerId || preview.Input.SessionId != sessionId ||
                preview.Input.Messages.Last().Text != envelope.Messages[0].Text)
                throw new InvalidDataException("Preview and captured envelope differ.");
            timeline.Add(new AgentConsoleTimelineMessage(preview.Input.MessageId,
                envelope.Messages[0].Role, envelope.Messages[0].Text,
                envelope.Messages[0].Timestamp, envelope.Source));
            var view = _assistant.Show(preview.ReplyPreviewId)
                ?? throw new InvalidDataException("Assistant preview view is missing.");
            provider.TryGetValue(preview.Input.MessageId, out var trace);
            var feedbackPath = Path.Combine(folder, "assistant-feedback.json");
            var feedback = File.Exists(feedbackPath)
                ? JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(feedbackPath), Json)?.Feedback.FeedbackType
                : null;
            views.Add(new AgentConsolePreviewRow(preview.Input.MessageId, view,
                trace?.Provider ?? (view.Decision?.RuleVersion.StartsWith("JEV/", StringComparison.Ordinal) == true
                    ? "JEV" : "RuleBased/Unknown"), trace?.FallbackUsed == true,
                trace?.IntentLatencyMilliseconds,
                preview.Input.CurrentProductContext ?? session.CurrentProductContext, feedback));
        }
        return new AgentConsoleConversation(session,
            timeline.OrderBy(item => item.Timestamp).ToArray(), views.OrderByDescending(item => item.View.CreatedAt).ToArray());
    }

    public string RequirePreviewSession(string previewId)
    {
        if (previewId.Length != 32 || !previewId.All(Uri.IsHexDigit))
            throw new ArgumentException("Preview ID must be 32 hex characters.", nameof(previewId));
        return Previews().SingleOrDefault(item => item.ReplyPreviewId == previewId)?.SessionId
            ?? throw new KeyNotFoundException("Preview is not part of this Shadow run.");
    }

    public void RequireVisibleSession(string sessionId)
    {
        if (!ListSessions().Any(item => item.SessionId == sessionId))
            throw new KeyNotFoundException("Session is not part of this Shadow run.");
    }
    private MessageSession ReadSession(string sessionId)
    {
        if (sessionId.Length != 64 || !sessionId.All(Uri.IsHexDigit))
            throw new ArgumentException("SessionId must be 64 hex characters.", nameof(sessionId));
        var path = Path.Combine(_state, "sessions", sessionId, "session.json");
        var session = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Session is unreadable.");
        if (session.SessionId != sessionId) throw new InvalidDataException("Session identity mismatch.");
        return session;
    }

    private IReadOnlyList<ReplyPreview> Previews()
    {
        var outbox = Path.Combine(_state, "outbox");
        if (!Directory.Exists(outbox)) return [];
        var found = new List<ReplyPreview>();
        foreach (var folder in Directory.EnumerateDirectories(outbox))
        {
            var path = Path.Combine(folder, "reply-preview.json");
            if (!File.Exists(path)) continue;
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(path), Json);
            if (preview is null || preview.CaptureId is null ||
                !preview.CaptureId.StartsWith(_runName + ":", StringComparison.Ordinal)) continue;
            if (preview.Input.MessageId != Path.GetFileName(folder) || preview.SendStatus != "Shadow")
                throw new InvalidDataException("Preview identity or send status is invalid.");
            found.Add(preview);
        }
        return found;
    }

    private Dictionary<string, AgentPipelineEvent> ProviderEvents()
    {
        var results = new Dictionary<string, AgentPipelineEvent>(StringComparer.Ordinal);
        foreach (var path in TracePaths())
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<AgentPipelineEvent>(line, Json);
            if (item?.Event == "INTENT_PROVIDER_RESULT" && item.MessageId is not null)
                results[item.MessageId] = item;
        }
        return results;
    }

    private IEnumerable<string> TracePaths()
    {
        var archive = Path.Combine(_run, "log-archive");
        if (Directory.Exists(archive))
            foreach (var path in Directory.EnumerateFiles(archive, "agent-pipeline.*.jsonl").Order())
                yield return path;
        var active = Path.Combine(_run, "agent-pipeline.jsonl");
        if (File.Exists(active)) yield return active;
    }
}
