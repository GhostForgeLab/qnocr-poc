using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed record MessageNoveltyDecision(int MessageIndex, string Fingerprint, string ConversationMessageKey, string Disposition);
public sealed record NewMessageComparison(bool IsBaseline, IReadOnlyList<BuyerMessage> NewMessages,
    IReadOnlyList<MessageNoveltyDecision> Decisions);

/// <summary>Session-local, single-conversation observation. Never resets on capture/layout failures.</summary>
public sealed class NewMessageTracker(DateTime observationStartedAt)
{
    private readonly HashSet<string> _fingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _messageKeys = new(StringComparer.Ordinal);
    private bool _hasBaseline;
    // Screenshot timestamp resolution is seconds. The operator sends only after baseline readiness.
    private readonly DateTime _notBefore = new(observationStartedAt.Ticks - observationStartedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);

    public NewMessageComparison Compare(IReadOnlyList<BuyerMessage> messages, DateTime observedAt)
    {
        var baseline = !_hasBaseline;
        var decisions = new List<MessageNoveltyDecision>();
        var added = new List<BuyerMessage>();
        foreach (var (message, index) in messages.Select((m, i) => (m, i)))
        {
            var fingerprint = MessageFingerprint.Create(message);
            var key = MessageFingerprint.ConversationMessageKey(message);
            string disposition;
            if (_fingerprints.Contains(fingerprint)) disposition = "DuplicateFingerprint";
            else if (_messageKeys.Contains(key)) disposition = "ContextChangedOnly";
            else if (baseline) disposition = "Baseline";
            else if (message.Timestamp < _notBefore) disposition = "HistoricalBeforeObservation";
            else if (message.Timestamp > observedAt.AddSeconds(5)) disposition = "FutureTimestampReview";
            else { disposition = "New"; added.Add(message); }
            // Future timestamps may be transient OCR errors: do not poison subsequent valid observations.
            if (disposition != "FutureTimestampReview") { _fingerprints.Add(fingerprint); _messageKeys.Add(key); }
            decisions.Add(new(index, fingerprint, key, disposition));
        }
        _hasBaseline = true;
        return new(baseline, added.AsReadOnly(), decisions.AsReadOnly());
    }
}
