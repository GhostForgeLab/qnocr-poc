using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed partial class AgentConsoleReadService
{
    private readonly bool _agentMode;

    private IReadOnlyList<AgentReplyBatchResult> AgentBatches()
    {
        var frames = Path.Combine(_run, "frames");
        if (!Directory.Exists(frames)) return [];
        var batches = new List<AgentReplyBatchResult>();
        foreach (var path in Directory.EnumerateFiles(frames, "reply_*.json", SearchOption.AllDirectories).Order())
        {
            if (Path.GetFileName(path).StartsWith("reply_replay_", StringComparison.Ordinal)) continue;
            var batch = JsonSerializer.Deserialize<AgentReplyBatchResult>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Agent capture receipt is unreadable.");
            var session = batch.Knowledge.Session;
            if (session.SessionId.Length != 32 || !session.SessionId.All(Uri.IsHexDigit) ||
                batch.Results.Any(item => item.Knowledge.SessionId != session.SessionId ||
                    item.Knowledge.SellerId != session.Identity.SellerId ||
                    item.Preview is { } preview && (preview.SessionId != session.SessionId ||
                        preview.BuyerId != session.Identity.BuyerId)))
                throw new InvalidDataException("Agent capture receipt association mismatch.");
            batches.Add(batch);
        }
        return batches;
    }

    private string AgentFolder(ReplyPreview preview) => Path.Combine(_state, "outbox",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preview.SessionId + ":" + preview.Input.MessageId))));

    private ReplyPreview ValidateAgentPreview(AgentReplyMessageResult result)
    {
        var expected = result.Preview!;
        var preview = JsonSerializer.Deserialize<ReplyPreview>(
            File.ReadAllText(Path.Combine(AgentFolder(expected), "reply-preview.json")), Json)
            ?? throw new InvalidDataException("Published Agent preview is missing.");
        if (preview.SendStatus != "Shadow" || preview.SessionId != result.Knowledge.SessionId ||
            preview.Input.SessionId != preview.SessionId || preview.Input.MessageId != result.Knowledge.MessageId ||
            preview.ReplyPreviewId != expected.ReplyPreviewId ||
            JsonSerializer.Serialize(preview) != JsonSerializer.Serialize(expected))
            throw new InvalidDataException("Agent preview differs from its capture receipt.");
        return preview;
    }

    private IReadOnlyList<AgentConsoleSessionRow> AgentSessions() => AgentBatches()
        .GroupBy(batch => batch.Knowledge.Session.SessionId, StringComparer.Ordinal).Select(group =>
        {
            var session = group.Last().Knowledge.Session;
            return new AgentConsoleSessionRow(session.SessionId, session.Identity.BuyerId,
                session.Messages.LastOrDefault()?.Content ?? "", session.LastActiveTime,
                group.SelectMany(batch => batch.Results).Count(result => result.Preview is not null));
        }).OrderByDescending(row => row.UpdatedAt).ToArray();

    private AgentConsoleConversation AgentConversation(string sessionId)
    {
        var batches = AgentBatches().Where(batch => batch.Knowledge.Session.SessionId == sessionId).ToArray();
        if (batches.Length == 0) throw new KeyNotFoundException("Session is not part of this capture.");
        var snapshot = batches[^1].Knowledge.Session;
        var results = batches.SelectMany(batch => batch.Results).ToArray();
        var latest = results.LastOrDefault();
        var product = latest?.Knowledge.ProductContext.Current;
        var session = new MessageSession(sessionId, snapshot.Identity.SellerId, snapshot.Identity.BuyerId,
            snapshot.Identity.BuyerId, "Observed", product?.ProductId ?? "", null,
            CreatedAt: snapshot.CreatedTime, LastActivityAt: snapshot.LastActiveTime,
            CurrentProductContext: product, ProductContextBindingStatus: latest?.Knowledge.ProductContext.Status);
        var timeline = batches.SelectMany(batch => batch.Results.Select(result =>
                batch.Knowledge.Session.Messages.Single(message => message.MessageId == result.Knowledge.MessageId)))
            .DistinctBy(message => message.MessageId)
            .Select(message => new AgentConsoleTimelineMessage(message.MessageId, message.Role, message.Content,
                message.Timestamp, message.Source)).OrderBy(message => message.Timestamp).ToArray();
        var previews = results.Where(result => result.Preview is not null).Select(result =>
        {
            var preview = ValidateAgentPreview(result);
            var view = _assistant.Show(preview.ReplyPreviewId)
                ?? throw new InvalidDataException("Assistant preview view is missing.");
            var feedbackPath = Path.Combine(AgentFolder(preview), "assistant-feedback.json");
            var feedback = File.Exists(feedbackPath)
                ? JsonSerializer.Deserialize<AssistantFeedbackRecord>(File.ReadAllText(feedbackPath), Json)?.Feedback.FeedbackType : null;
            return new AgentConsolePreviewRow(preview.Input.MessageId, view, result.Knowledge.Intent.Provider,
                result.Knowledge.Intent.FallbackUsed, result.Knowledge.Intent.LatencyMilliseconds,
                preview.Input.CurrentProductContext, feedback);
        }).OrderByDescending(row => row.View.CreatedAt).ToArray();
        var notices = results.Where(result => result.Preview is null)
            .Select(result => $"消息 {result.Knowledge.MessageId}：{result.Status}（{result.Error ?? "未生成建议"}）").ToArray();
        return new AgentConsoleConversation(session, timeline, previews, CanClearProduct: false, Notices: notices);
    }
}
