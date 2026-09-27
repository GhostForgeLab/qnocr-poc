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
        {
            var bound = i.Session.ProductContextBindingStatus == ProductContextBindingStatus.Bound
                ? i.Session.CurrentProductContext : null;
            var id = bound?.ProductId ?? (ProductIdExtractor.IsValidProductId(i.Envelope.ProductId)
                ? i.Envelope.ProductId : "");
            return new MessageProductLink(i.MessageId, id, i.CaptureId ?? "", i.CaptureTime,
                bound is null ? i.Envelope.ProductId.Length == 0 ? "NoVisibleProduct" : i.Envelope.ProductBinding
                    : "CurrentSessionProductContext:" + bound.Source);
        })).ToArray();
        var currentBound = current.Session.ProductContextBindingStatus == ProductContextBindingStatus.Bound
            ? current.Session.CurrentProductContext : null;
        var currentProductId = currentBound?.ProductId ?? current.Envelope.ProductId;
        return new(current.Session.SessionId, current.Session.BuyerId, currentProductId, messages,
            current.CaptureTime, current.CaptureId ?? "", current.MessageId,
            current.AiRequestId ?? MessageFingerprint.AiRequestId(current.MessageId), Math.Min(10, messages.Length),
            current.Session.BuyerDisplayName ?? current.Session.ObservedBuyer,
            current.Session.CreatedAt == default ? current.CaptureTime : current.Session.CreatedAt,
            current.Session.LastActivityAt == default ? current.CaptureTime : current.Session.LastActivityAt,
            references, links, current.Session.Identity,
            currentBound, current.Session.ProductContextBindingStatus);
    }
}
