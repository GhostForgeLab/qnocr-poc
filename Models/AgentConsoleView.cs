namespace QnocrPoc.Models;

public sealed record AgentConsoleSessionRow(string SessionId, string Buyer,
    string LastMessage, DateTimeOffset UpdatedAt, int PreviewCount);

public sealed record AgentConsoleTimelineMessage(string MessageId, string Role,
    string Text, DateTime Timestamp, string Source);

public sealed record AgentConsolePreviewRow(string MessageId, AssistedReplyView View,
    string IntentProvider, bool FallbackUsed, double? IntentLatencyMilliseconds,
    BoundProductContext? ProductContext, string? FeedbackType);

public sealed record AgentConsoleConversation(MessageSession Session,
    IReadOnlyList<AgentConsoleTimelineMessage> Timeline,
    IReadOnlyList<AgentConsolePreviewRow> Previews,
    bool CanClearProduct = true, IReadOnlyList<string>? Notices = null);
