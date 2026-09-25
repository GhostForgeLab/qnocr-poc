using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public static class SessionContextBuilder
{
    public static SessionContextView Build(DispatchItem current, IEnumerable<DispatchItem> known)
    {
        var history = known.Where(i => i.Session.SessionId == current.Session.SessionId && i.MessageId != current.MessageId)
            .OrderBy(i => i.CaptureTime).ThenBy(i => i.MessageId, StringComparer.Ordinal)
            .SelectMany(i => i.Envelope.Messages).ToArray();
        var messages = history.Concat(current.Envelope.Messages).ToArray();
        if (messages.Length == 0) throw new InvalidDataException("AI session has no buyer message.");
        return new(current.Session.SessionId, current.Session.BuyerId, current.Envelope.ProductId, messages,
            current.CaptureTime, current.CaptureId ?? "", current.MessageId,
            current.AiRequestId ?? MessageFingerprint.AiRequestId(current.MessageId), Math.Min(10, messages.Length));
    }
}
