using QnocrPoc.Models;

namespace QnocrPoc.Product;

/// <summary>Resolves current-session evidence; never searches historical product references.</summary>
public interface IProductContextResolver
{
    BoundProductContext? Resolve(MessageSession session, OcrResult? productOcr,
        DateTimeOffset capturedAt, string captureId);
}
