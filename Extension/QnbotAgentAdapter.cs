using QnocrPoc.Models;

namespace QnocrPoc.Extension;

/// <summary>Narrow, read-only handoff for a future qnbot sidecar. No Bot assembly or send API reference.</summary>
public sealed class QnbotAgentAdapter(IAgentExtension extension)
{
    private readonly IAgentExtension _extension = extension ?? throw new ArgumentNullException(nameof(extension));
    private readonly HashSet<string> _seenNativeIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) throw new InvalidOperationException("Agent extension was already initialized.");
        _extension.Initialize(new AgentExtensionOptions());
        _initialized = true;
    }

    public async Task<AgentExtensionReceipt> ObserveAsync(QnbotInboundObservation observed,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the extension first.");
        ArgumentNullException.ThrowIfNull(observed);
        cancellationToken.ThrowIfCancellationRequested();
        var correlation = Correlation(observed);
        AgentExtensionReceipt Held(string reason) => new("Held", correlation, Reason: reason);
        if (observed.Role != "buyer") return Held("NotBuyer");
        if (string.IsNullOrWhiteSpace(observed.Text) || observed.Timestamp == default ||
            observed.Text.Length > 4000) return Held("InvalidTextOrTime");
        if (string.IsNullOrWhiteSpace(observed.SellerId) || string.IsNullOrWhiteSpace(observed.BuyerId) ||
            string.IsNullOrWhiteSpace(observed.SellerNick) || string.IsNullOrWhiteSpace(observed.BuyerNick) ||
            observed.SellerNick == observed.BuyerNick) return Held("IdentityMappingMissing");
        if (observed.IdentityStatus == "Verified")
        {
            if (string.IsNullOrWhiteSpace(observed.NativeBuyerTargetId) ||
                string.IsNullOrWhiteSpace(observed.ConversationCode) ||
                string.IsNullOrWhiteSpace(observed.EvidenceId)) return Held("NativeIdentityEvidenceMissing");
        }
        else if (observed.IdentityStatus != "IsolatedTest") return Held("IdentityUnverified");
        if (string.IsNullOrWhiteSpace(observed.NativeMessageId)) return Held("NativeMessageIdMissing");
        lock (_gate)
        {
            if (_seenNativeIds.Count >= 10000) return Held("ObservationCapacityReached");
            if (!_seenNativeIds.Add(correlation))
                return new AgentExtensionReceipt("Duplicate", correlation, Reason: "NativeMessageIdAlreadyObserved");
        }
        var envelope = new MessageEnvelope("Qnbot.Native.ReadOnly", observed.SellerId, observed.BuyerId,
            "", [new EnvelopeMessage("buyer", observed.Text, observed.Timestamp)]);
        var evidence = new AgentExtensionEvidence(correlation, observed.NativeMessageId,
            observed.ConversationCode, observed.NativeBuyerTargetId, observed.IdentityStatus,
            observed.EvidenceId, DateTimeOffset.UtcNow);
        try
        {
            var receipt = await _extension.ProcessMessageAsync(envelope, evidence, cancellationToken);
            if (receipt.CorrelationId != correlation || receipt.SendEnabled || receipt.SentMessages != 0)
                throw new InvalidDataException("Agent extension returned an unsafe or mismatched receipt.");
            return receipt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // The extension may have observed the message before failing. Never replay automatically.
            return new AgentExtensionReceipt("Uncertain", correlation, Reason: ex.GetType().Name);
        }
    }

    public ReplyPreview? GetReplyPreview(string correlationId)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the extension first.");
        lock (_gate)
        {
            if (!_seenNativeIds.Contains(correlationId)) return null;
        }
        var preview = _extension.GetReplyPreview(correlationId);
        if (preview is not null && preview.SendStatus != "Shadow")
            throw new InvalidDataException("Only Shadow previews may cross the extension boundary.");
        return preview;
    }

    private static string Correlation(QnbotInboundObservation observed) =>
        observed.SellerId + ":" + observed.BuyerId + ":" + observed.NativeMessageId;
}
