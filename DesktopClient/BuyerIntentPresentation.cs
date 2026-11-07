using System;
using System.Collections.Generic;
using System.Linq;
using QnocrPoc.Models;

namespace QNAgent;

internal sealed record JevPurpose(string Name, JevChoice Judgement);
internal sealed record BuyerIntentPresentation(string Summary, string Confidence, string Guidance, bool Uncertain);
internal static class BuyerIntentDescriptions
{
    private sealed record Description(string Criteria, string Summary, string Guidance);
    private static readonly Dictionary<string, Description> Items = new()
    {
        ["ACKNOWLEDGE"] = new("只是表示收到、了解或感谢，没有新问题或明确下单承诺。单独的好的、嗯嗯、知道了通常属于此项。", "表示已了解，暂时没有新问题", "简短回应即可，不重复介绍商品或追问。"),
        ["END_CONVERSATION"] = new("明确暂时不需要、拒绝或结束咨询，如不用了、先不买。", "想暂时结束咨询", "尊重对方意愿，礼貌收尾，不继续推销。"),
        ["CONFIRM_SELECTION"] = new("明确确认款式、规格、数量或购买选择；上一条卖家提出明确选择时，买家的对应简短答复也可确认选择。没有对应上下文时不能仅凭好的猜测下单，更不能声称订单已创建。", "正在确认购买选择", "确认具体选择，缺少的必要信息再询问。"),
        ["ASK_DIFFERENCES"] = new("想了解两款或多款差异，以便比较选择。", "想比较不同款式，帮助选择", "突出有资料支持的主要差异。"),
        ["CHECK_FIT"] = new("想确认商品能否装下、适配某物品或满足具体使用条件。", "想确认是否适合自己的使用需求", "核对具体款式和使用条件，不猜测兼容性。"),
        ["CLARIFY_PRODUCT"] = new("想弄清卖的到底是什么，可能把容器或配件当成内装商品或主体商品。", "可能误认商品，想确认实际售卖内容", "先说明商品类别与已确认的售卖范围。"),
        ["ASK_PRICE"] = new("想确认价格、优惠、是否划算或最终费用。", "想确认价格或优惠条件", "按对应规格说明价格和适用条件。"),
        ["ASK_DELIVERY"] = new("想确认何时发货、何时收到、物流或配送条件。", "想确认发货或到货安排", "说明已知时效，订单状态不明时先核实。"),
        ["SEEK_AFTER_SALES"] = new("遇到使用、损坏、退换或保修问题，需要售后处理。", "需要解决售后问题", "先确认具体问题，再按实际售后政策处理。"),
        ["ASK_CUSTOMIZATION"] = new("希望修改、刻字、定制或询问定制条件。", "想确认能否定制", "说明已确认的定制范围和条件。"),
        ["EXPRESS_CONCERN"] = new("表达不满、质疑或担忧，当前重点是解决顾虑，不推断未表达的情绪。", "有疑问或顾虑，希望得到解释", "先回应明确说出的顾虑，再解释有据的信息。"),
        ["ASK_RECOMMENDATION"] = new("希望商家帮助推荐或选择适合自己的款式。", "希望得到选款建议", "先结合用途和偏好推荐，不替买家假定需求。"),
        ["ASK_DETAILS"] = new("询问具体尺寸、材质、功能、数量、保养等信息，没有更明确的其他目的。", "想了解商品的具体信息", "直接回答当前问题，缺什么再补问。"),
        ["GREETING"] = new("仅打招呼或确认是否有人接待，没有商品问题。", "打招呼或确认是否有人接待", "简短回应，等待对方说明需求。"),
        ["UNCLEAR"] = new("当前话语和可见上下文不足，无法判断具体诉求。", "暂时无法明确判断具体需求", "先确认对方想咨询什么，不猜测心理活动。")
    };
    internal static IReadOnlyDictionary<string, string> Criteria { get; } = Items.ToDictionary(p => p.Key, p => p.Value.Criteria);
    internal static bool IsSocial(JevPurpose? purpose) => purpose is not null && purpose.Name is "ACKNOWLEDGE" or "END_CONVERSATION" or "GREETING" &&
        purpose.Judgement.Probability >= .75 && purpose.Judgement.Confidence >= .50;
    internal static string Guidance(JevPurpose? purpose) => purpose is not null && Items.TryGetValue(purpose.Name, out var description) ? description.Guidance : "依据当前问题和资料回答，不猜测买家需求。";
    internal static BuyerIntentPresentation Present(JevPurpose? purpose, IntentResult? legacy = null)
    {
        if (purpose is null)
        {
            var topic = legacy?.IntentName switch
            {
                "PRODUCT_SPEC_QUERY" => "询问商品规格或尺寸", "MATERIAL_QUERY" => "询问商品材质", "PRICE_QUERY" => "询问价格或优惠",
                "PRODUCT_IDENTITY_QUERY" => "可能需要确认商品身份", "COMPATIBILITY_QUERY" => "询问是否适配", "SHIPPING_QUERY" => "询问发货或配送",
                "AFTER_SALES_QUERY" => "咨询售后问题", "CUSTOMIZATION_QUERY" => "咨询定制", "USAGE_QUERY" => "咨询使用方法", _ => "尚无明确意图判断"
            };
            return new(topic, "仅有主题分类", "重新生成后可查看本次 JEV 意图判断。", true);
        }
        if (!Items.TryGetValue(purpose.Name, out var item) || purpose.Name == "UNCLEAR" || purpose.Judgement.Probability < .55 || purpose.Judgement.Confidence < .35)
            return new("当前意图不够明确", "需确认", "先确认对方的具体需求，不将推测当作事实。", true);
        var summary = purpose.Name == "ASK_DETAILS" ? legacy?.IntentName switch
        {
            "PRODUCT_SPEC_QUERY" => "想了解具体尺寸或规格", "MATERIAL_QUERY" => "想确认商品材质",
            "USAGE_QUERY" => "想了解使用或保养方法", _ => item.Summary
        } : item.Summary;
        return new(summary, $"把握 {purpose.Judgement.Probability:P0}", item.Guidance, false);
    }
}
