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
    private readonly Queue<(string Fingerprint, string Key, DateTime At)> _order = new();
    // Screenshot timestamp resolution is seconds. The operator sends only after baseline readiness.
    private readonly DateTime _notBefore = observationStartedAt.AddMinutes(-2);
    private DateTime _baselineSourceTime = DateTime.MinValue;

    public NewMessageComparison Compare(IReadOnlyList<BuyerMessage> messages, DateTime observedAt)
    {
        var baseline = !_hasBaseline;
        while (_order.Count > 0 && (_order.Count > 10000 || _order.Peek().At < observedAt.AddHours(-24)))
        {
            var old = _order.Dequeue(); _fingerprints.Remove(old.Fingerprint); _messageKeys.Remove(old.Key);
        }
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
            else if (message.Timestamp < _notBefore || message.Timestamp < observedAt.AddMinutes(-2) || message.Timestamp < _baselineSourceTime) disposition = "HistoricalBeforeObservation";
            else if (message.Timestamp > observedAt.AddMinutes(2)) disposition = "FutureTimestampReview";
            else { disposition = "New"; added.Add(message); }
            // Future timestamps may be transient OCR errors: do not poison subsequent valid observations.
            if (disposition != "FutureTimestampReview")
            {
                if (_fingerprints.Add(fingerprint)) _order.Enqueue((fingerprint, key, observedAt));
                _messageKeys.Add(key);
            }
            decisions.Add(new(index, fingerprint, key, disposition));
        }
        _hasBaseline = true;
        if (baseline && messages.Count > 0) _baselineSourceTime = messages.Max(m => m.Timestamp);
        return new(baseline, added.AsReadOnly(), decisions.AsReadOnly());
    }

    public void ForgetUncommitted(IEnumerable<BuyerMessage> messages)
    {
        foreach (var message in messages) { _fingerprints.Remove(MessageFingerprint.Create(message)); _messageKeys.Remove(MessageFingerprint.ConversationMessageKey(message)); }
    }
}
