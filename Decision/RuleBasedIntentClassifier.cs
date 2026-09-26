using System.Text;
using QnocrPoc.Models;

namespace QnocrPoc.Decision;

/// <summary>Deterministic routing hints; no product facts, knowledge lookup, AI call, or send decision.</summary>
public sealed class RuleBasedIntentClassifier : IIntentClassifier
{
    public IntentResult Classify(SessionContextView context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Messages.LastOrDefault(m => m.Role.Equals("buyer", StringComparison.OrdinalIgnoreCase))?.Text
            ?.Normalize(NormalizationForm.FormKC).ToLowerInvariant() ?? "";
        // More specific after-sales wording wins over a customization keyword in the same sentence.
        if (Has(text, "售后", "退货", "退款", "退换", "能退", "可以退", "保修", "坏了", "换货"))
            return Result("AFTER_SALES_QUERY", true, true, true, "High", "after-sales");
        if (Has(text, "规格", "容量", "毫升", "ml", "尺寸", "型号", "多大", "大容量"))
            return Result("PRODUCT_SPEC_QUERY", true, true, true, "Medium", "product-spec");
        if (Has(text, "材质", "材料", "不锈钢", "304", "玻璃", "塑料"))
            return Result("MATERIAL_QUERY", true, true, true, "Medium", "material");
        if (Has(text, "刻字", "定制", "刻印", "印字", "logo"))
            return Result("CUSTOMIZATION_QUERY", true, true, true, "Medium", "customization");
        if (Has(text, "发货", "配送", "快递", "多久到", "几天到", "物流"))
            return Result("SHIPPING_QUERY", false, true, true, "Medium", "shipping");
        if (Has(text, "价格", "多少钱", "优惠", "折扣", "便宜"))
            return Result("PRICE_QUERY", true, true, true, "Medium", "price");
        return Result("OTHER", false, false, true, "Medium", "fallback");
    }

    private static bool Has(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.Ordinal));
    private static IntentResult Result(string intent, bool product, bool knowledge, bool human,
        string risk, string rule) => new(intent, null, product, knowledge, human, risk, MatchedRule: rule);
}
