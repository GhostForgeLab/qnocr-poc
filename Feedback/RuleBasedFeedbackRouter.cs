using System.Text.RegularExpressions;
using QnocrPoc.Models;

namespace QnocrPoc.Feedback;

/// <summary>Conservative, deterministic routing of human feedback; never edits a target system.</summary>
public sealed class RuleBasedFeedbackRouter : IFeedbackRouter
{
    private static readonly Regex Capacity = new(@"(?i)\d+(?:\.\d+)?\s?(?:ml|毫升)",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly string[] Uncertain = ["不知道", "不确定", "无法确认", "请以页面为准", "以页面为准", "没看到", "未看到", "需确认"];
    private static readonly string[] Material = ["TA1", "304", "纯钛", "不锈钢", "玻璃", "塑料"];

    public FeedbackClassification Classify(ReplyFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        FeedbackClassification Result(string type, string reason, string target) =>
            new(feedback.ReplyPreviewId, type, null, reason, target, DateTimeOffset.Now);
        if (feedback.FeedbackType == ReplyFeedbackTypes.Accepted)
            return Result(FeedbackClassificationTypes.Other, "AcceptedNoHumanCorrection", FeedbackTargets.None);
        var note = feedback.OperatorNote ?? "";
        if (Has(note, "意图识别", "意图判断", "分类错误", "理解错问题") ||
            feedback.Reason == "信息不准确" && IntentMismatch(feedback.QuestionCategory, feedback.DecisionIntent))
            return Result(FeedbackClassificationTypes.IntentIssue, "HumanFlaggedIntentMismatch", FeedbackTargets.Decision);
        if (feedback.Reason == "缺少商品信息")
            return Result(FeedbackClassificationTypes.KnowledgeGap, "HumanMissingProductInformation", FeedbackTargets.Knowledge);
        if (feedback.QuestionCategory is "商品规格" or "材质" && AddedProductFact(feedback))
            return Result(FeedbackClassificationTypes.KnowledgeGap, "HumanAddedProductFact", FeedbackTargets.Knowledge);
        if (feedback.Reason == "语气问题" || Has(note, "太官方", "语气问题", "表达太生硬"))
            return Result(FeedbackClassificationTypes.StyleIssue, "HumanToneRevision", FeedbackTargets.Prompt);
        if ((feedback.QuestionCategory == "售后" || Has(feedback.BuyerQuestion, "退款", "退货", "退换", "保修")) &&
            (feedback.Reason == "信息不准确" || Has(note, "规则错误", "售后规则", "退款规则")))
            return Result(FeedbackClassificationTypes.RuleIssue, "HumanCorrectedAfterSalesRule", FeedbackTargets.Strategy);
        if (feedback.OriginalAiReply.Contains("尊敬的客户", StringComparison.Ordinal) &&
            !feedback.FinalHumanReply.Contains("尊敬的客户", StringComparison.Ordinal))
            return Result(FeedbackClassificationTypes.StyleIssue, "HumanShortenedFormalGreeting", FeedbackTargets.Prompt);
        return Result(FeedbackClassificationTypes.Other, "NoStrongRoutingSignal", FeedbackTargets.None);
    }

    private static bool AddedProductFact(ReplyFeedback feedback)
    {
        var ai = feedback.OriginalAiReply; var final = feedback.FinalHumanReply;
        if (string.IsNullOrWhiteSpace(final) || final == ai) return false;
        var aiUncertain = Uncertain.Any(term => ai.Contains(term, StringComparison.Ordinal));
        var capacity = Capacity.Matches(final).Select(match => match.Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (capacity.Any(token => !ai.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
            aiUncertain && capacity.Length > 0) return true;
        return Material.Any(token => final.Contains(token, StringComparison.OrdinalIgnoreCase) &&
            (aiUncertain || !ai.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IntentMismatch(string category, string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent)) return false;
        var expected = category switch
        {
            "商品规格" => "PRODUCT_SPEC_QUERY", "材质" => "MATERIAL_QUERY", "价格" => "PRICE_QUERY",
            "发货" => "SHIPPING_QUERY", "售后" => "AFTER_SALES_QUERY", _ => null
        };
        return expected is not null && intent != expected;
    }
    private static bool Has(string text, params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.Ordinal));
}
