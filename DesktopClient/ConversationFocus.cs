using System;
using System.Collections.Generic;
using System.Linq;
using QnocrPoc.Extension.AgentContext;

namespace QNAgent;

internal sealed record ObservedConversation(string SellerId, string? Buyer, string Detail, long? CaptureVersion = null, bool PreserveDisplay = false);
internal sealed class ConversationFocus
{
    public string SellerId { get; private set; } = "";
    public string? Buyer { get; private set; }
    public long Revision { get; private set; }
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    public void Observe(ObservedConversation observation)
    {
        if (SellerId != observation.SellerId || Buyer != observation.Buyer) Revision++;
        SellerId = observation.SellerId; Buyer = observation.Buyer;
    }
    public bool Contains(InboxMessage message) => Buyer is not null && message.SellerId == SellerId &&
        new IdentityNormalizer().Normalize(message.Buyer) == new IdentityNormalizer().Normalize(Buyer);
    public InboxMessage[] Messages(IEnumerable<InboxMessage> source) => source.Where(Contains)
        .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Sequence).Take(10).ToArray();
    public void Remember(InboxMessage? message, string draft)
    {
        if (message is null || string.IsNullOrEmpty(draft)) return;
        _drafts[message.MessageId] = draft;
        if (_drafts.Count > 200) _drafts.Remove(_drafts.Keys.First());
    }
    public string? Draft(InboxMessage message) => _drafts.TryGetValue(message.MessageId, out var text) ? text : null;
    public void ClearDraft(InboxMessage message) => _drafts.Remove(message.MessageId);
}
