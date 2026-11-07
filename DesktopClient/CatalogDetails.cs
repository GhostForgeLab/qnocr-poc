using System;
using System.IO;
using System.Linq;

namespace QNAgent;

public sealed record ProductVariant
{
    public string Name { get; init; } = "";
    public decimal? Price { get; init; }
    public string Details { get; init; } = "";
}
public sealed record ProductFaq
{
    public string Question { get; init; } = "";
    public string Answer { get; init; } = "";
}
public sealed record Promotion
{
    public string Title { get; init; } = "";
    public string AppliesTo { get; init; } = "";
    public string Rule { get; init; } = "";
    public string Validity { get; init; } = "";
    public string Stacking { get; init; } = "";
}
public sealed record ShopKnowledge
{
    public string SellerId { get; init; } = "";
    public Promotion[] Promotions { get; init; } = Array.Empty<Promotion>();
    public bool Approved { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
internal static class CatalogValidation
{
    private static bool Invalid(string? text, int limit, bool required = false) => text is null || text.Length > limit || required && string.IsNullOrWhiteSpace(text);
    internal static void ValidatePromotions(Promotion[]? rows)
    {
        if (rows is null || rows.Length > 50 || rows.Any(p => p is null || Invalid(p.Title, 200, true) || Invalid(p.Rule, 2000, true) ||
            Invalid(p.AppliesTo, 1000, true) || Invalid(p.Validity, 500, true) || Invalid(p.Stacking, 500, true)))
            throw new InvalidDataException("优惠最多 50 条，请填写名称、规则、适用范围、有效期与叠加规则；尚未确认的条件请明确填写“待核实”。");
    }
    internal static void ValidateShop(ShopKnowledge shop)
    {
        if (shop is null) throw new InvalidDataException("店铺优惠不能为空。");
        if (Invalid(shop.SellerId, 128, true) || shop.SellerId.Any(char.IsControl)) throw new InvalidDataException("请填写有效的店铺标识。");
        ValidatePromotions(shop.Promotions);
    }
    internal static void ValidateDetails(CatalogProduct product)
    {
        if (product is null) throw new InvalidDataException("商品资料不能为空。");
        if (product.Variants is null || product.Variants.Length > 200 || product.Variants.Any(v => v is null || Invalid(v.Name, 200, true) ||
            v.Price is < 0 or > 99999999 || v.Price.HasValue && decimal.Round(v.Price.Value, 2) != v.Price || Invalid(v.Details, 2000)))
            throw new InvalidDataException("规格最多 200 条；请填写规格名称，价格留空表示待核实，或填写非负金额（最多两位小数）。");
        if (product.Variants.GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("规格名称重复，请为每个款式填写不同的名称。");
        if (product.Faqs is null || product.Faqs.Length > 100 || product.Faqs.Any(f => f is null || Invalid(f.Question, 500, true) || Invalid(f.Answer, 4000, true)))
            throw new InvalidDataException("问答最多 100 条，每条都需要填写问题和答案（分别最多 500、4000 字）。");
        ValidatePromotions(product.Promotions);
    }
    internal static bool HasKnowledge(CatalogProduct product, string field) =>
        field.Length == 0 || !string.IsNullOrWhiteSpace(product.Fields()[field]) || !string.IsNullOrWhiteSpace(product.Notes) || product.Faqs.Length > 0 ||
        field == "Capacity" && product.Variants.Length > 0 || field == "CurrentPrice" && (product.Variants.Any(v => v.Price.HasValue) || product.Promotions.Length > 0);
}
