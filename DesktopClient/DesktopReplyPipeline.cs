using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Models;

namespace QNAgent;

internal sealed record DesktopReplyOutcome(string Reply, IntentResult Intent, string Mode, bool Guarded,
    bool ModelUsed, JevAssessment? Assessment = null, JevVerification? Verification = null, string? Failure = null, string? RejectedDraft = null,
    string? EvidenceMethod = null, ReplyCallTrace? Calls = null);

internal static class DesktopReplyPipeline
{
    internal const string Version = "confirmed-product-learning-v1";
    internal static async Task<DesktopReplyOutcome> RunAsync(ClientSettings settings, string evidence,
        bool hasProduct, IJevGateway jev, CancellationToken token, Action<JevAssessment>? onAnalyzed = null) =>
        (await RunOptionsAsync(settings, evidence, hasProduct, jev, token, 1, onAnalyzed))[0];

    internal static async Task<DesktopReplyOutcome[]> RunOptionsAsync(ClientSettings settings, string evidence,
        bool hasProduct, IJevGateway jev, CancellationToken token, int count = 3, Action<JevAssessment>? onAnalyzed = null)
    {
        var calls = new ReplyCallTrace { ReplyModel = settings.AiModel }; var watch = Stopwatch.StartNew();
        try
        {
            var results = await RunCoreAsync(settings, evidence, hasProduct, jev, token, count, calls, onAnalyzed);
            return results.Select(r => r with { Calls = calls }).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return FallbackOptions(new("（本次未生成回复建议，请重试服务请求）",
                new IntentResult(calls.JevIntent ?? "OTHER", null, true, true, true, "High", "JEV/" + calls.JevModel),
                "REPLY_UNAVAILABLE", true, false, Assessment: calls.AnalysisSnapshot, Failure: error is ReplyModelException modelError ? modelError.Code : error.GetType().Name, Calls: calls), count);
        }
        finally { calls.ElapsedMilliseconds = watch.ElapsedMilliseconds; }
    }
    private static async Task<DesktopReplyOutcome[]> RunCoreAsync(ClientSettings settings, string evidence,
        bool hasProduct, IJevGateway jev, CancellationToken token, int count, ReplyCallTrace calls, Action<JevAssessment>? onAnalyzed)
    {
        if (count is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(count));
        token.ThrowIfCancellationRequested();
        JevAssessment assessment;
        try
        {
            calls.JevAnalysisAttempted = true;
            assessment = await jev.AnalyzeAsync(settings, evidence, token);
            calls.JevAnalysisSucceeded = true; calls.JevModel = assessment.Model;
            calls.JevIntent = assessment.Intent.IntentName;
            calls.AnalysisSnapshot = assessment;
            onAnalyzed?.Invoke(assessment);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return FallbackOptions(new(ReplyTextPolicy.HumanReply, new IntentResult("OTHER", null, true, true, true, "High", "JEVUnavailable"),
                "JEV_UNAVAILABLE", true, false, Failure: error.GetType().Name), count);
        }
        var mode = assessment.Mode;
        // No product facts can be answered without a confirmed current product. A question may still clarify identity.
        if (!hasProduct && mode is "ANSWER" or "PARTIAL" or "IDENTITY_CLARIFICATION") mode = "CLARIFY_BUYER";
        if (!hasProduct && mode != "SOCIAL_REPLY")
            return FallbackOptions(new(ReplyTextPolicy.HumanReply, assessment.Intent, "HUMAN_CHECK", true, false, assessment), count);
        var planned = JsonNode.Parse(evidence)!.AsObject();
        planned["ReplyPlan"] = new JsonObject { ["Intent"] = assessment.Intent.IntentName, ["Mode"] = mode,
            ["CommunicationGoal"] = assessment.Purpose?.Name, ["Guidance"] = BuyerIntentDescriptions.Guidance(assessment.Purpose) };
        if (mode == "SOCIAL_REPLY") { planned["Product"] = null; planned["ShopPromotions"] = null; }
        string[] drafts = Array.Empty<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var raw = await Connections.ChatAsync(settings, planned.ToJsonString(), token, alternatives: count == 3, calls: calls);
            try { drafts = ParseDrafts(raw, count); }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
            { drafts = Array.Empty<string>(); }
            if (drafts.Length == count && drafts.All(ReplyTextPolicy.Allowed)) break;
            planned["StyleCorrection"] = "上次格式或用词不合要求。严格按本次格式输出；仅烟盒允许含烟字，其他改用物品或原包装。不增加事实。";
        }
        if (drafts.Length != count || drafts.Any(d => !ReplyTextPolicy.Allowed(d)))
            return FallbackOptions(Fallback(assessment, mode, "STYLE_REJECTED"), count);
        JevVerification[] verifications;
        try
        {
            calls.VerificationAttempted = true;
            verifications = count == 1 ? new[] { await jev.VerifyAsync(settings, evidence, drafts[0], mode, token) }
                : await jev.VerifyManyAsync(settings, evidence, drafts, mode, token);
            if (verifications.Length != count) throw new InvalidDataException("回复核验数量不一致。");
            calls.VerificationSucceeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return FallbackOptions(Fallback(assessment, mode, "VERIFY_UNAVAILABLE", error.GetType().Name), count); }
        var fallbacks = FallbackOptions(Fallback(assessment, mode, "UNSUPPORTED_REPLY"), count);
        var results = drafts.Select((draft, i) => verifications[i].Accepted
            ? new DesktopReplyOutcome(draft, assessment.Intent, mode, mode is not ("ANSWER" or "IDENTITY_CLARIFICATION" or "SOCIAL_REPLY"), true, assessment, verifications[i])
            : JevGateway.CanOfferForHumanReview(verifications[i].Support)
                ? new DesktopReplyOutcome(draft, assessment.Intent, "REVIEW_DRAFT", true, true, assessment, verifications[i], EvidenceMethod: "HumanReviewRequired")
            : fallbacks[i] with { Verification = verifications[i], RejectedDraft = draft }).ToArray();
        var direct = ExplicitProductFacts.Answer(evidence, assessment.Intent.IntentName);
        if (direct is not null)
            for (var i = 0; i < results.Length; i++)
                if (!results[i].ModelUsed)
                    results[i] = new(direct, assessment.Intent, "DIRECT_FACTS", false, false, assessment, verifications[i],
                        RejectedDraft: drafts[i], EvidenceMethod: "CopiedApprovedFact");
        var verified = results.FirstOrDefault(r => r.Verification?.Accepted == true);
        if (count == 3 && verified is not null)
        {
            // An ungrounded style variation must not add new facts. Reuse an already-verified answer
            // with a neutral greeting/question instead of offering a contradictory generic fallback.
            var used = results.Where(r => r.Verification?.Accepted == true).Select(r => r.Reply).ToHashSet(StringComparer.Ordinal);
            foreach (var i in Enumerable.Range(0, count).Where(i => !verifications[i].Accepted))
            {
                var variants = new[] { "您好，" + verified.Reply, verified.Reply + "您还想了解哪方面呢？", "这边跟您说明一下：" + verified.Reply };
                var replacement = variants.First(text => !used.Contains(text)); used.Add(replacement);
                results[i] = verified with { Reply = replacement, EvidenceMethod = "SameVerifiedFacts", RejectedDraft = drafts[i] };
            }
        }
        return results;
    }
    internal static string[] ParseDrafts(string raw, int count)
    {
        string[] drafts;
        if (count == 1) drafts = new[] { ReplyTextPolicy.Normalize(raw) };
        else
        {
            using var json = JsonDocument.Parse(raw);
            drafts = json.RootElement.GetProperty("replies").EnumerateArray().Select(r => ReplyTextPolicy.Normalize(r.GetString() ?? "")).ToArray();
        }
        if (drafts.Length != count || drafts.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 2000) || drafts.Distinct(StringComparer.Ordinal).Count() != count)
            throw new InvalidDataException("需要三条不同的有效短回复。");
        return drafts;
    }
    internal static DesktopReplyOutcome[] FallbackOptions(DesktopReplyOutcome first, int count)
    {
        if (count == 1) return new[] { first };
        var clarify = first.Reply == ReplyTextPolicy.ClarifyReply || first.Mode == "VISUAL_CLARIFICATION";
        var options = clarify
            ? new[] { first.Reply, "方便再说明一下您想了解的具体内容吗？我确认后再答复您。", "您具体想咨询哪方面呢？请补充一点信息，我再帮您核实。" }
            : new[] { first.Reply, "我先确认一下这方面的信息，核实后再准确回复您。", "目前还不能确定这个问题的答案，需要进一步核实，避免给您错误的信息。" };
        return options.Select(text => first with { Reply = text }).ToArray();
    }
    private static DesktopReplyOutcome Fallback(JevAssessment assessment, string mode, string reason, string? failure = null) =>
        mode == "SOCIAL_REPLY"
            ? new(assessment.Purpose?.Name == "GREETING" ? "您好，在的。" : "好的，有需要随时联系。", assessment.Intent, "SOCIAL_REPLY", false, false,
                assessment, Failure: failure ?? reason, EvidenceMethod: "SocialCourtesy")
            : new(mode == "CLARIFY_BUYER" ? ReplyTextPolicy.ClarifyReply : ReplyTextPolicy.HumanReply,
                assessment.Intent, reason, true, false, assessment, Failure: failure);
}
