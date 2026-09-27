using QnocrPoc.Models;

namespace QnocrPoc.Product;

/// <summary>Combines only current manual and same-capture OCR evidence; history is never a candidate.</summary>
public sealed class SessionProductContextResolver(IProductContextResolver manual,
    IProductContextResolver ocr)
{
    public ProductContextResolution Resolve(MessageSession session, OcrResult? productOcr,
        DateTimeOffset capturedAt, string captureId)
    {
        var chosen = manual.Resolve(session, productOcr, capturedAt, captureId);
        var observed = ocr.Resolve(session, productOcr, capturedAt, captureId);
        if (productOcr is not null && new ProductIdExtractor().Extract(productOcr).Status
            is ProductIdStatus.Ambiguous or ProductIdStatus.LowConfidence)
            return new(null, ProductContextBindingStatus.Uncertain,
                "Current OCR product ID is ambiguous or low confidence; knowledge is held.");
        if (chosen is not null && observed is not null && chosen.ProductId != observed.ProductId)
            return new(null, ProductContextBindingStatus.Conflict,
                "Manual product differs from the currently visible OCR product; knowledge is held.");
        if (chosen is not null) return new(chosen, ProductContextBindingStatus.Bound, "Current operator choice.");
        if (observed is not null) return new(observed, ProductContextBindingStatus.Bound,
            "Unique labeled product ID in the current capture.");
        return new(null, ProductContextBindingStatus.Unknown,
            "No current operator choice or reliable same-capture product ID.");
    }
}
