using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Models;

namespace QNAgent;

internal static class RealCatalogReplayChecks
{
    public static async Task<int> RunAsync(string root, bool comparisonOnly = false)
    {
        var shared = new ClientStore(Path.Combine(root, "Output", "DesktopClient"));
        var actual = shared.Products().Single(p => p.ProductId == "985503282040" && p.Approved);
        // Preserve all real product facts. Replace store/buyer identifiers and never write into customer history.
        var product = actual with { SellerId = "catalog-replay" };
        var settings = shared.Settings() with { SellerId = product.SellerId };
        var folder = Path.Combine(root, "Output", "RealCatalogReplay", Guid.NewGuid().ToString("N"));
        var testStore = new ClientStore(folder); var service = new ReplyService(testStore);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        foreach (var question in comparisonOnly ? new[] { "细支烟盒跟粗支啥区别" } : new[] { "这是一条还是一盒？", "尺寸是多少" })
        {
            var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.Now;
            var message = new InboxMessage("replay-session", id, product.SellerId, "replay-buyer", question, now.DateTime, product.ProductId, "replay", false);
            var context = new SessionContextView(message.SessionId, "replay-buyer", product.ProductId,
                new[] { new EnvelopeMessage("buyer", question, now.DateTime) }, now, "replay", id, id, 1);
            var result = await service.GenerateAsync(message, context, product, settings, deadline.Token);
            var audit = service.ReadAudit(result)!;
            Console.WriteLine($"Question: {question}\nReply: {result.EffectiveReply}\nJEV: {audit.Calls?.JevModel}, initial={audit.Assessment?.Coverage}, replyModel={audit.Calls?.ActualReplyModel ?? audit.Calls?.ReplyModel}, successfulRequests={audit.Calls?.ReplyRequestsSucceeded}, finish={audit.Calls?.LastFinishReason}, retries={string.Join(",", audit.Calls?.RetryReasons ?? new())}, failure={audit.Calls?.ReplyFailureCode}, review={audit.Verification?.Support}, mode={audit.Mode}, evidence={audit.EvidenceMethod}");
            if (audit.Calls?.JevAnalysisSucceeded != true || audit.Calls.ReplyRequestsSucceeded < 1 ||
                !service.IsCurrentPipeline(result) || service.Options(message).Length != 1 || !ReplyTextPolicy.Allowed(result.EffectiveReply) || result.EffectiveReply.Contains('*'))
                throw new Exception("Replay failed to run both models or failed single/plain-text policy.");
            if (comparisonOnly)
            {
                if (!result.EffectiveReply.Contains("粗支") || !result.EffectiveReply.Contains("细支") ||
                    !new[] { "尺寸", "长", "厚", "薄", "包装" }.Any(result.EffectiveReply.Contains) || !audit.ModelUsed)
                    throw new Exception("Real comparison question was not answered from the supplied differences.");
            }
            else if (question.Contains("尺寸"))
            {
                if (!new[] { "92", "60", "26", "109", "58", "13" }.All(result.EffectiveReply.Contains) || result.EffectiveReply.Contains("资料不足"))
                    throw new Exception("Real variant dimensions were not answered.");
            }
            else if (!result.EffectiveReply.Contains("烟盒") || !(result.EffectiveReply.Contains("不含") || result.EffectiveReply.Contains("不包含") || result.EffectiveReply.Contains("不是")))
                throw new Exception("Real product identity was not clarified.");
        }
        Console.WriteLine("Real catalog replay passed for both reported questions. Evidence: " + folder);
        return 0;
    }
}
