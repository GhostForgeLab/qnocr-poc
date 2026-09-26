using QnocrPoc.Models;
using QnocrPoc.Product;

namespace QnocrPoc.Shadow;

public static class SessionContextBuilder
{
    public static SessionContextView Build(DispatchItem current, IEnumerable<DispatchItem> known)
    {
        var history = known.Where(i => i.Session.SessionId == current.Session.SessionId && i.MessageId != current.MessageId)
            .OrderBy(i => i.CaptureTime).ThenBy(i => i.MessageId, StringComparer.Ordinal).ToArray();
        var timeline = history.Append(current).ToArray();
        var messages = timeline.SelectMany(i => i.Envelope.Messages).ToArray();
        if (messages.Length == 0) throw new InvalidDataException("AI session has no buyer message.");
        var references = timeline.SelectMany(i => i.Session.ProductReferences ?? Array.Empty<ProductReference>())
            .Concat(timeline.Where(i => ProductIdExtractor.IsValidProductId(i.Envelope.ProductId))
                .Select(i => new ProductReference(i.Envelope.ProductId, "Qianniu.OCR.EnvelopeSidecar",
                    i.CaptureTime, i.CaptureId ?? "", i.Envelope.ProductBinding)))
            .Where(reference => ProductIdExtractor.IsValidProductId(reference.ProductId))
            .GroupBy(reference => reference.ProductId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(reference => reference.CapturedAt).First())
            .OrderBy(reference => reference.CapturedAt).ToArray();
        var links = timeline.SelectMany(i => i.Envelope.Messages.Select(_ =>
            new MessageProductLink(i.MessageId,
                ProductIdExtractor.IsValidProductId(i.Envelope.ProductId) ? i.Envelope.ProductId : "",
                i.CaptureId ?? "", i.CaptureTime,
                i.Envelope.ProductId.Length == 0 ? "NoVisibleProduct" : i.Envelope.ProductBinding))).ToArray();
        return new(current.Session.SessionId, current.Session.BuyerId, current.Envelope.ProductId, messages,
            current.CaptureTime, current.CaptureId ?? "", current.MessageId,
            current.AiRequestId ?? MessageFingerprint.AiRequestId(current.MessageId), Math.Min(10, messages.Length),
            current.Session.BuyerDisplayName ?? current.Session.ObservedBuyer,
            current.Session.CreatedAt == default ? current.CaptureTime : current.Session.CreatedAt,
            current.Session.LastActivityAt == default ? current.CaptureTime : current.Session.LastActivityAt,
            references, links, current.Session.Identity);
    }
}
