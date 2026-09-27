using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Field names are interface metadata, not facts or a knowledge-base schema.</summary>
public static class KnowledgeRequestBuilder
{
    public static KnowledgeRequest Build(SessionContextView context, IntentResult decision, string sellerId = "")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        var question = context.Messages.LastOrDefault(m => m.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase))?.Text ?? "";
        var bindingStatus = context.ProductContextBindingStatus;
        ProductReference? product;
        if (bindingStatus is not null)
        {
            var bound = context.CurrentProductContext;
            // OCR evidence must refer to this capture. Manual selection is explicitly session-scoped.
            var reliable = bindingStatus == ProductContextBindingStatus.Bound && bound is not null &&
                bound.SessionId == context.SessionId && bound.SellerId == sellerId &&
                bound.ProductId == context.ProductId &&
                (bound.Source is ProductContextSource.Manual or ProductContextSource.OCR) &&
                QnocrPoc.Product.ProductIdExtractor.IsValidProductId(bound.ProductId) &&
                (bound.Source != ProductContextSource.OCR || bound.CaptureId == context.CaptureId) &&
                (bound.Source != ProductContextSource.OCR || bound.Confidence is >= 0.80);
            if (reliable)
                product = new ProductReference(bound!.ProductId, "CurrentSessionProductContext:" + bound.Source,
                    bound.CapturedAt, context.CaptureId, "BoundCurrentSession");
            else
            {
                product = null;
                bindingStatus = bindingStatus == ProductContextBindingStatus.Bound
                    ? ProductContextBindingStatus.Uncertain : bindingStatus;
            }
        }
        else
        {
            // Legacy test contexts predate explicit binding and retain their existing current-frame behavior.
            product = string.IsNullOrWhiteSpace(context.ProductId) ? null :
                context.ProductReferences?.FirstOrDefault(r => r.ProductId == context.ProductId && r.CaptureId == context.CaptureId)
                ?? new ProductReference(context.ProductId, "Qianniu.OCR.CurrentContext", context.CapturedAt,
                    context.CaptureId, "ObservedNotVerified");
        }
        var waterproof = question.Contains("防水", StringComparison.Ordinal) ||
            question.Contains("waterproof", StringComparison.OrdinalIgnoreCase);
        var fields = waterproof ? new[] { "Waterproof" } : decision.IntentName switch
        {
            "PRODUCT_SPEC_QUERY" => question.Contains("ml", StringComparison.OrdinalIgnoreCase)
                || question.Contains("毫升", StringComparison.Ordinal) || question.Contains("容量", StringComparison.Ordinal)
                    ? new[] { "Capacity" } : new[] { "Specification" },
            "MATERIAL_QUERY" => ["Material"],
            "SHIPPING_QUERY" => ["ShippingTime"],
            "AFTER_SALES_QUERY" or "AFTERSALE_QUERY" => ["ReturnPolicy"],
            "PRICE_QUERY" => ["CurrentPrice"],
            "CUSTOMIZATION_QUERY" => ["CustomizationPolicy"],
            _ => decision.NeedKnowledge ? ["Unspecified"] : Array.Empty<string>()
        };
        return new KnowledgeRequest(context.SessionId, product, decision.IntentName, question,
            fields, context.MessageId, context.CaptureId, DateTimeOffset.Now, sellerId, bindingStatus);
    }
}
