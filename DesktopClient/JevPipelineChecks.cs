using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QNAgent;

internal static class JevPipelineChecks
{
    private const string Evidence = "{\"BuyerQuestion\":\"这款多少钱，能连原包装放进去吗？\",\"Conversation\":[],\"Product\":{\"ProductId\":\"123\",\"Variants\":[{\"Name\":\"粗支款\",\"Price\":248,\"Details\":\"外尺寸92×60×26mm\"}]},\"ShopPromotions\":null}";
    internal static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        Check(ReplyTextPolicy.Normalize("**粗支款**：92*60*26mm") == "粗支款：92×60×26mm", "Markdown removed without damaging dimension multiplication");
        Check(ReplyTextPolicy.Allowed("这款烟盒有粗支款和细支款。") && !ReplyTextPolicy.Allowed("把烟拿出来") && !ReplyTextPolicy.Allowed("烟盒可以装香烟"), "word policy allows product name only, including mixed sentences");
        Check(JevGateway.SupportAccepted(new JevChoice("SUPPORTED", .82, .73)), "supported comparison at observed confidence remains a usable human-reviewed draft");
        Check(!JevGateway.SupportAccepted(new JevChoice("SUPPORTED", .70, .65)) &&
            !JevGateway.SupportAccepted(new JevChoice("UNSUPPORTED", .99, .99)) && !JevGateway.SupportAccepted(new JevChoice("UNCERTAIN", .99, .99)),
            "low-confidence, unsupported and uncertain factual drafts remain blocked");
        Check(JevGateway.CanOfferForHumanReview(new JevChoice("SUPPORTED", .78, .67)) &&
            !JevGateway.CanOfferForHumanReview(new JevChoice("UNSUPPORTED", .99, .99)) &&
            !JevGateway.CanOfferForHumanReview(new JevChoice("UNCERTAIN", .99, .99)) &&
            !JevGateway.CanOfferForHumanReview(new JevChoice("SUPPORTED", .55, .40)),
            "supported intermediate-confidence text can be reviewed, but unsupported and uncertain text cannot");
        const string dimensions = "{\"BuyerQuestion\":\"尺寸是多少\",\"Product\":{\"Variants\":[{\"Details\":\"大款尺寸:12*20*30mm\"},{\"Details\":\"小款尺寸:10*15*25mm\"}]}}";
        Check(ExplicitProductFacts.Answer(dimensions, "PRODUCT_SPEC_QUERY") == "大款尺寸：12×20×30mm；小款尺寸：10×15×25mm。", "exact dimension fallback copies only labelled variant dimensions");
        Check(ExplicitProductFacts.Answer(dimensions.Replace("尺寸是多少", "内部尺寸是多少"), "PRODUCT_SPEC_QUERY") is null,
            "dimension quoting never treats unspecified dimensions as internal dimensions");
        Check(ExplicitProductFacts.Answer(dimensions.Replace("小款尺寸", "大款尺寸"), "PRODUCT_SPEC_QUERY") is null,
            "conflicting dimensions cannot be silently merged");
        async Task<(DesktopReplyOutcome Result, int Chats, DesktopReplyOutcome[] Options)> Scenario(string mode, string draft, string verdict = "SUPPORTED",
            double probability = 0.99, bool analyzeFailure = false, bool reviewFailure = false, string? repaired = null, bool malformed = false, bool three = false, double reviewProbability = 0.99)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            int chats = 0; bool analyzed = false;
            var server = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await listener.GetContextAsync();
                        using var reader = new StreamReader(request.Request.InputStream);
                        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                        object response;
                        if (request.Request.Url!.AbsolutePath == "/jev")
                        {
                            var questions = body.RootElement.GetProperty("questions");
                            bool analyze = questions.TryGetProperty("intent", out _);
                            if (analyze) analyzed = true;
                            if (analyze ? analyzeFailure : reviewFailure)
                            { request.Response.StatusCode = 500; request.Response.Close(); continue; }
                            var answers = new Dictionary<string, object>();
                            foreach (var q in questions.EnumerateObject())
                            {
                                var options = q.Value.GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToArray();
                                var chosen = q.Name == "intent" ? "COMPATIBILITY_QUERY" : q.Name == "purpose" ? "ASK_DETAILS" : q.Name == "coverage" ? mode : three && q.Name != "support0" ? "UNSUPPORTED" : verdict;
                                var p = q.Name == "coverage" ? probability : q.Name.StartsWith("support", StringComparison.Ordinal) ? reviewProbability : 0.99;
                                var distribution = options.ToDictionary(o => o, o => o == chosen ? p : (1 - p) / (options.Length - 1));
                                if (malformed) distribution[chosen] = 9;
                                answers[q.Name] = new { type = "choice", choice = chosen, confidence = p, probabilities = distribution };
                            }
                            response = new { model = "jev-fixture", answers };
                        }
                        else
                        {
                            if (!analyzed) throw new Exception("reply model ran before JEV analysis");
                            chats++;
                            using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                            if (prompt.RootElement.GetProperty("ReplyPlan").GetProperty("Mode").GetString() != (probability < (mode == "PARTIAL" ? 0.70 : 0.80) ? "EVIDENCE_FIRST" : mode))
                                throw new Exception("reply generator lost JEV mode");
                            var content = chats > 1 && repaired is not null ? repaired : draft;
                            if (three) content = JsonSerializer.Serialize(new { replies = new[] { draft, "错误候选二", "错误候选三" } });
                            response = new { choices = new[] { new { message = new { content } } } };
                        }
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                        request.Response.ContentType = "application/json"; await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
                    }
                }
                catch (Exception error) when (!listener.IsListening && error is HttpListenerException or ObjectDisposedException) { }
            });
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var settings = new ClientSettings { JevEndpoint = $"http://127.0.0.1:{port}/jev", AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "fixture" };
                var options = await DesktopReplyPipeline.RunOptionsAsync(settings, Evidence, true, new JevGateway(), deadline.Token, three ? 3 : 1);
                return (options[0], chats, options);
            }
            finally { listener.Stop(); await server; }
        }
        var sufficient = await Scenario("ANSWER", "**粗支款**价格是248元。");
        Check(sufficient.Result.Reply == "粗支款价格是248元。" && sufficient.Result.Verification?.Accepted == true && sufficient.Chats == 1, "JEV analysis precedes generation and supported plain text passes review");
        var reviewDraft = await Scenario("ANSWER", "粗支款248元。", reviewProbability: .78);
        Check(reviewDraft.Result.Mode == "REVIEW_DRAFT" && reviewDraft.Result.ModelUsed && reviewDraft.Result.Guarded &&
            reviewDraft.Result.Reply == "粗支款248元。" && reviewDraft.Result.Verification?.Accepted == false,
            "supported intermediate-confidence draft stays visible with an explicit human-review state");
        var mixedOptions = await Scenario("ANSWER", "粗支款248元。", three: true);
        Check(mixedOptions.Options.Length == 3 && mixedOptions.Options.All(r => r.Reply.Contains("248") && !r.Reply.Contains("错误候选")) &&
            mixedOptions.Options.Count(r => r.EvidenceMethod == "SameVerifiedFacts") == 2 && mixedOptions.Options.Select(r => r.Reply).Distinct().Count() == 3,
            "rejected variations reuse only verified facts with neutral wording");
        var missing = await Scenario("HUMAN_CHECK", ReplyTextPolicy.HumanReply);
        Check(missing.Chats == 1 && missing.Result.Reply == ReplyTextPolicy.HumanReply && missing.Result.Calls?.ReplyRequestsSucceeded == 1, "missing seller evidence still reaches reply model for a contextual safe reply");
        var clarify = await Scenario("CLARIFY_BUYER", "您咨询的是粗支款还是细支款？");
        Check(clarify.Result.Mode == "CLARIFY_BUYER" && clarify.Result.Guarded && clarify.Result.Reply == "您咨询的是粗支款还是细支款？", "buyer ambiguity follows clarification mode and is reviewed");
        var partial = await Scenario("PARTIAL", "粗支款248元。是否能连原包装放入还需要核实内部尺寸。", probability: 0.73);
        Check(partial.Result.Mode == "PARTIAL" && partial.Result.Reply.Contains("248") && partial.Result.Guarded, "partial evidence permits supported facts while holding uncertain remainder");
        var partialUncertain = await Scenario("PARTIAL", "已知部分按资料说明，其他部分待确认。", probability: 0.60);
        Check(partialUncertain.Chats == 1 && partialUncertain.Result.Mode == "EVIDENCE_FIRST", "uncertain initial classification uses evidence-first generation and final verification");
        var unsupported = await Scenario("ANSWER", "软包可以直接带盒放，硬盒需要撕盖。", "UNSUPPORTED");
        Check(!unsupported.Result.ModelUsed && unsupported.Result.Mode == "UNSUPPORTED_REPLY" && unsupported.Result.Reply == ReplyTextPolicy.HumanReply, "unsupported compatibility claims are withheld after generation");
        var uncertain = await Scenario("ANSWER", "粗支款248元，其他问题还需要核实。", probability: 0.45);
        Check(uncertain.Chats == 1 && uncertain.Result.Mode == "EVIDENCE_FIRST" && uncertain.Result.Verification?.Accepted == true, "low confidence initial routing no longer prevents a grounded reply");
        var unavailable = await Scenario("ANSWER", "不得调用", analyzeFailure: true);
        Check(unavailable.Chats == 0 && unavailable.Result.Mode == "JEV_UNAVAILABLE", "JEV outage does not silently use keyword routing");
        var failedReview = await Scenario("ANSWER", "粗支款248元。", reviewFailure: true);
        Check(failedReview.Result.Mode == "VERIFY_UNAVAILABLE" && failedReview.Result.Reply == ReplyTextPolicy.HumanReply, "review outage withholds unverified generated text");
        var styleRepair = await Scenario("ANSWER", "烟盒可以装烟。", repaired: "这款烟盒的价格是248元。");
        Check(styleRepair.Chats == 2 && styleRepair.Result.Reply == "这款烟盒的价格是248元。", "restricted wording receives one regeneration then evidence review");
        var styleRejected = await Scenario("ANSWER", "烟可以放进去。");
        Check(styleRejected.Result.Mode == "STYLE_REJECTED" && ReplyTextPolicy.Allowed(styleRejected.Result.Reply), "repeated restricted wording is withheld");
        var invalid = await Scenario("ANSWER", "不得调用", malformed: true);
        Check(invalid.Chats == 0 && invalid.Result.Mode == "JEV_UNAVAILABLE", "invalid JEV probability distributions cannot authorize generation");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await DesktopReplyPipeline.RunAsync(new ClientSettings(), Evidence, true, new JevGateway(), cancelled.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "cancellation propagates through JEV workflow"); }
        Console.WriteLine($"Passed {count} JEV pipeline checks with controlled HTTP; no external model called.");
        return 0;
    }
    internal static async Task<int> LiveAsync()
    {
        // Synthetic facts only. Uses existing TypeSafe credentials, never a real buyer conversation.
        var settings = new ClientSettings(); var jev = new JevGateway();
        var cases = new[]
        {
            ("material", "{\"BuyerQuestion\":\"这个是什么材质？\",\"Product\":{\"Fields\":{\"Material\":\"纯钛\"}}}", "ANSWER"),
            ("outer-size-only", "{\"BuyerQuestion\":\"我选粗支款，能带原包装直接放进去吗？\",\"Product\":{\"Variants\":[{\"Name\":\"粗支款\",\"Details\":\"外尺寸92×60×26mm\"}]}}", "HUMAN_CHECK"),
            ("variant-ambiguity", "{\"BuyerQuestion\":\"这个多少钱？\",\"Product\":{\"Variants\":[{\"Name\":\"A\",\"Price\":248},{\"Name\":\"B\",\"Price\":298}]}}", "CLARIFY_BUYER")
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        foreach (var (name, state, expected) in cases)
        {
            var result = await jev.AnalyzeAsync(settings, state, deadline.Token);
            Console.WriteLine($"Live JEV {name}: model={result.Model}, intent={result.Intent.IntentName}, mode={result.Mode}, probability={result.Coverage.Probability:F3}");
            if (result.Mode != expected) throw new Exception($"Live fixture {name} expected {expected}.");
        }
        var review = await jev.VerifyAsync(settings, Evidence, "软包可以直接连包装放入，硬盒撕掉盖子就能放。", "ANSWER", deadline.Token);
        Console.WriteLine($"Live JEV unsupported-claim review: model={review.Model}, verdict={review.Support.Choice}, accepted={review.Accepted}");
        if (review.Accepted) throw new Exception("Unsupported compatibility claim passed live review.");
        Console.WriteLine("Live JEV synthetic evidence checks passed."); return 0;
    }
    internal static async Task<int> LiveReplyAsync(string root)
    {
        var settings = new ClientStore(Path.Combine(root, "Output", "DesktopClient")).Settings();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var cases = new[]
        {
            ("known-material", "{\"BuyerQuestion\":\"这款烟盒是什么材质？\",\"Conversation\":[],\"Product\":{\"Name\":\"测试烟盒\",\"Fields\":{\"Material\":\"纯钛\"}},\"ShopPromotions\":null}", "ANSWER"),
            ("partial-evidence", Evidence.Replace("这款多少钱，能连原包装放进去吗？", "我选粗支款。请分别回答：粗支款多少钱？粗支款能连原包装一起放入吗？"), "PARTIAL"),
            ("clarify-variant", "{\"BuyerQuestion\":\"这个多少钱？\",\"Conversation\":[],\"Product\":{\"Variants\":[{\"Name\":\"粗支款\",\"Price\":248},{\"Name\":\"细支款\",\"Price\":298}]},\"ShopPromotions\":null}", "CLARIFY_BUYER")
        };
        foreach (var (name, state, mode) in cases)
        {
            var result = await DesktopReplyPipeline.RunAsync(settings, state, true, new JevGateway(), deadline.Token);
            Console.WriteLine($"Live pipeline {name}: mode={result.Mode}, raw={result.Assessment?.Coverage}, JEV={result.Assessment?.Model}, modelUsed={result.ModelUsed}, verified={result.Verification?.Accepted}");
            Console.WriteLine("Synthetic reply: " + result.Reply);
            if (name == "partial-evidence" && result.Mode == "HUMAN_CHECK" && result.Guarded && result.Assessment?.Coverage.Choice == "PARTIAL")
            {
                Console.WriteLine("Partial judgment was uncertain and correctly held for human verification.");
                continue;
            }
            if (result.Mode != mode || !result.ModelUsed || result.Verification?.Accepted != true || !ReplyTextPolicy.Allowed(result.Reply) || result.Reply.Contains('*'))
                throw new Exception("Live reply fixture failed: " + name);
        }
        Console.WriteLine("Live configured reply-model + JEV checks passed using synthetic product data."); return 0;
    }
    internal static async Task<int> LiveOptionsAsync(string root)
    {
        var settings = new ClientStore(Path.Combine(root, "Output", "DesktopClient")).Settings();
        var jev = new JevGateway(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        const string identity = "{\"BuyerQuestion\":\"这是一条还是一盒？\",\"Conversation\":[],\"Product\":{\"Name\":\"纯钛烟盒\",\"Fields\":{\"SalesScope\":\"仅销售烟盒，属于收纳用品，不含内装物品。\"}},\"ShopPromotions\":null}";
        var replies = await DesktopReplyPipeline.RunOptionsAsync(settings, identity, true, jev, deadline.Token);
        foreach (var r in replies) Console.WriteLine($"Live identity: intent={r.Intent.IntentName}, mode={r.Mode}, evidence={r.EvidenceMethod}, support={r.Verification?.Support}; {r.Reply}; rejected={r.RejectedDraft}");
        if (replies.Length != 3 || replies.Any(r => r.Intent.IntentName != "PRODUCT_IDENTITY_QUERY" || r.Mode != "IDENTITY_CLARIFICATION" ||
            r.EvidenceMethod != "DirectApprovedSalesScope" || !ReplyTextPolicy.Allowed(r.Reply) || !r.Reply.Contains("烟盒")) || replies.Select(r => r.Reply).Distinct().Count() != 3)
            throw new Exception("Identity clarification or three reviewed alternatives failed.");
        var quantity = await jev.AnalyzeAsync(settings, identity.Replace("这是一条还是一盒？", "一个订单发几个盒子？我就是问盒子的数量。"), deadline.Token);
        Console.WriteLine($"Live explicit box quantity: intent={quantity.Intent.IntentName}, mode={quantity.Mode}");
        if (quantity.Mode == "IDENTITY_CLARIFICATION") throw new Exception("Ordinary box quantity was mistaken for product identity confusion.");
        var unsupported = await jev.VerifyManyAsync(settings, identity, new[] { "这里卖的是烟盒收纳用品，不含内装物品。", "一单发十个盒子。", "您好，这款是烟盒，您想了解哪种款式？" }, "IDENTITY_CLARIFICATION", deadline.Token);
        Console.WriteLine("Live per-option review: " + string.Join(", ", unsupported.Select(v => v.Support.Choice)));
        if (unsupported[0].Support.Choice != "SUPPORTED" || unsupported[1].Accepted || unsupported[2].Support.Choice != "SUPPORTED") throw new Exception("Per-option grounding checks failed.");
        const string ordinary = "{\"BuyerQuestion\":\"这款是什么材质？\",\"Conversation\":[],\"Product\":{\"Name\":\"收纳盒\",\"Fields\":{\"Material\":\"纯钛\"}},\"ShopPromotions\":null}";
        var modelOptions = await DesktopReplyPipeline.RunOptionsAsync(settings, ordinary, true, jev, deadline.Token);
        foreach (var r in modelOptions) Console.WriteLine($"Live model alternatives: mode={r.Mode}, accepted={r.Verification?.Accepted}; {r.Reply}");
        if (modelOptions.Length != 3 || modelOptions.Any(r => !r.ModelUsed || r.Verification?.Accepted != true || !ReplyTextPolicy.Allowed(r.Reply)) ||
            modelOptions.Select(r => r.Reply).Distinct().Count() != 3) throw new Exception("Three distinct model-generated alternatives failed review.");
        Console.WriteLine("Live identity and three-option checks passed with synthetic data."); return 0;
    }
}
