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
        var product = string.IsNullOrWhiteSpace(context.ProductId) ? null :
            context.ProductReferences?.FirstOrDefault(r => r.ProductId == context.ProductId && r.CaptureId == context.CaptureId)
            ?? new ProductReference(context.ProductId, "Qianniu.OCR.CurrentContext", context.CapturedAt,
                context.CaptureId, "ObservedNotVerified");
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
            fields, context.MessageId, context.CaptureId, DateTimeOffset.Now, sellerId);
    }
}
