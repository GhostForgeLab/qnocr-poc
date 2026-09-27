using QnocrPoc.Models;

namespace QnocrPoc.Extension;

/// <summary>Shadow-only boundary; implementation may publish previews but has no send capability.</summary>
public interface IAgentExtension
{
    void Initialize(AgentExtensionOptions options);
    Task<AgentExtensionReceipt> ProcessMessageAsync(MessageEnvelope envelope,
        AgentExtensionEvidence evidence, CancellationToken cancellationToken = default);
    ReplyPreview? GetReplyPreview(string correlationId);
}

public sealed record AgentExtensionOptions(bool ShadowOnly = true, bool SendEnabled = false);

public sealed record AgentExtensionEvidence(string CorrelationId, string NativeMessageId,
    string? ConversationCode, string? NativeBuyerTargetId, string IdentityStatus,
    string EvidenceId, DateTimeOffset ObservedAt);

public sealed record AgentExtensionReceipt(string Status, string CorrelationId,
    string? ReplyPreviewId = null, bool SendEnabled = false, int SentMessages = 0,
    string? Reason = null);

/// <summary>Host-owned observation, not a qnbot QNChatMessage or a forged platform identity.</summary>
public sealed record QnbotInboundObservation(string SellerId, string BuyerId,
    string SellerNick, string BuyerNick, string Role, string Text, DateTime Timestamp,
    string NativeMessageId, string? ConversationCode, string? NativeBuyerTargetId,
    string IdentityStatus, string EvidenceId);
