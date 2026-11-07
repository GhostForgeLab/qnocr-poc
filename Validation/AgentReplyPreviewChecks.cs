using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class AgentReplyPreviewChecks
{
    private const string ProductA = "1051087533910", ProductB = "1111111111111", ProductC = "2222222222222";
    private const string TestKeyMarker = "CONTROLLED_V1350C_KEY_NEVER_LOG";
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public string? Text;
        public int Calls;
        public void SetText(string text) { Text = text; Calls++; }
    }
    private sealed class Fixture : IDisposable
    {
        public AgentContextKnowledgeChecks.Fixture Data { get; }
        public AgentReplyPipeline Pipeline { get; private set; }
        public string State { get; }
        public string Config { get; }
        public string Mode = "success";
        public bool TamperSendReceipt;
        public List<AgentReplyMessageResult> Results { get; } = [];
        private HttpClient _jev;
        public Fixture(string root, string name)
        {
            Data = new AgentContextKnowledgeChecks.Fixture(root, name);
            Data.Pipeline.Dispose(); // One Context owner; reuse only the existing validation data/transport fixture.
            State = Path.Combine(Data.Root, "reply-state");
            Config = Path.Combine(Data.Root, "ai-provider.json");
            File.Copy("Config/ai-provider.json", Config);
            _jev = new HttpClient(Data.Handler, disposeHandler: false);
            Pipeline = Create();
        }
        private AgentReplyPipeline Create() => new(Data.Database, Data.ProductState, Data.ProductFile,
            Data.Workflow.ItemsPath, State, Config, timeProvider: Data.Clock, jevClient: _jev,
            apiKey: () => "controlled-jev-key", generate: async (input, identity, output, token) =>
            {
                var reply = await new ShadowAdapterClient(aiShadow: true, aiProviderConfigPath: Config,
                    agentReplyPreview: true, controlledReplyMode: Mode).ConvertAsync(input, identity, output, token);
                if (TamperSendReceipt && reply.ReceiptPath is not null)
                {
                    var receipt = JsonNode.Parse(File.ReadAllText(reply.ReceiptPath))!.AsObject();
                    receipt["SendEnabled"] = true; // Negative metadata test only; no send implementation is invoked.
                    File.WriteAllText(reply.ReceiptPath, receipt.ToJsonString());
                }
                return reply;
            });
        public async Task<AgentReplyBatchResult> Process(MessageEnvelope envelope, OcrResult? ocr = null)
        {
            var result = await Pipeline.ProcessMessageAsync(envelope, ocr, "controlled-reply-capture", Data.Clock.GetUtcNow());
            Results.AddRange(result.Results);
            return result;
        }
        public void Restart()
        {
            Pipeline.Dispose(); _jev.Dispose();
            _jev = new HttpClient(Data.Handler, disposeHandler: false); Pipeline = Create();
        }
        public string Folder(ReplyPreview preview) => Directory.EnumerateDirectories(Path.Combine(State, "outbox"))
            .Single(folder => File.Exists(Path.Combine(folder, "reply-preview.json")) &&
                JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(folder, "reply-preview.json")))!.ReplyPreviewId == preview.ReplyPreviewId);
        public string Body(ReplyPreview preview) => File.ReadAllText(Path.Combine(Folder(preview), "provider", "controlled-provider-request.json"));
        public void Dispose() { Pipeline.Dispose(); _jev.Dispose(); Data.Dispose(); }
    }
    private static MessageEnvelope Envelope(string buyer, string text, int index, string seller = "seller-A") =>
        new("Qianniu.OCR", seller, buyer, "", [new EnvelopeMessage("buyer", text, new DateTime(2026, 10, 4, 12, 0, 0).AddSeconds(index))]);
    private static OcrResult Ocr(string product) => new("controlled-product-region", "fixture-only",
        [new OcrLine(1, "商品ID: " + product, .95f, new OcrBounds(0, 0, 250, 20))],
        0, .95, 0, 0, 0, 0, 0, 0);
    private static void Reject<T>(Action action, Action<bool, string> check, string text) where T : Exception
    {
        try { action(); } catch (T) { check(true, text); return; } check(false, text);
    }
    private static AgentReplyMessageResult One(AgentReplyBatchResult result) => result.Results.Single();
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1350C", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var fixtures = new List<Fixture>();
        Fixture New(string name) { var f = new Fixture(root, name); fixtures.Add(f); return f; }
        using (var f = New("complete-and-review"))
        {
            var workflowBefore = File.ReadAllText(f.Data.Workflow.ItemsPath);
            var envelope = Envelope("reply-A", "这个酒壶是什么材质", 1);
            var first = await f.Process(envelope, Ocr(ProductA));
            var result = One(first);
            var preview = result.Preview ?? throw new InvalidDataException("Expected a controlled Provider Preview.");
            var consoleRun = Path.Combine(f.Data.Root, "console-run");
            var consoleFrames = Path.Combine(consoleRun, "frames", "0001");
            Directory.CreateDirectory(consoleFrames);
            var consoleMetadata = Path.Combine(consoleRun, "session.json");
            File.WriteAllText(consoleMetadata, JsonSerializer.Serialize(new
            { Mode = "ShadowReplyPreview", StateDirectory = f.State, SendEnabled = false, SentMessages = 0 }));
            File.WriteAllText(Path.Combine(consoleFrames, "reply_0001.json"), JsonSerializer.Serialize(first));
            File.WriteAllText(Path.Combine(consoleFrames, "reply_replay_0001.json"), JsonSerializer.Serialize(first));
            var console = new AgentConsoleReadService(f.State, consoleRun);
            check(console.ListSessions().Single().PreviewCount == 1 &&
                console.RequirePreviewSession(preview.ReplyPreviewId) == preview.SessionId,
                "Agent web projection accepts new IDs and hashed folders without counting replay receipts");
            var consoleView = console.GetConversation(preview.SessionId);
            check(consoleView.Timeline.Single().Text == envelope.Messages[0].Text &&
                consoleView.Previews.Single().IntentProvider == "JEV" &&
                consoleView.Previews.Single().View.AiReply == preview.Reply && !consoleView.CanClearProduct,
                "Agent web conversation reads actual reply, intent and current capture message");
            Reject<KeyNotFoundException>(() => console.GetConversation(Guid.NewGuid().ToString("N")), check,
                "Agent console rejects sessions from outside the selected capture");
            Reject<InvalidDataException>(() => new AgentConsoleReadService(Path.Combine(f.Data.Root, "foreign-state"), consoleRun), check,
                "Agent console rejects a foreign review state directory");
            var failure = result with { Preview = null, Status = "AIUnavailable", Error = "ControlledError" };
            File.WriteAllText(Path.Combine(consoleFrames, "reply_0002.json"), JsonSerializer.Serialize(first with { Results = [failure] }));
            check(console.GetConversation(preview.SessionId).Notices!.Single().Contains("AIUnavailable"),
                "Agent console shows AI failures even when no preview is published");
            File.Delete(Path.Combine(consoleFrames, "reply_0002.json"));
            var publishedPath = Path.Combine(f.Folder(preview), "reply-preview.json");
            var originalPreview = File.ReadAllText(publishedPath);
            File.WriteAllText(publishedPath, JsonSerializer.Serialize(preview with { Reply = "tampered" }));
            Reject<InvalidDataException>(() => console.GetConversation(preview.SessionId), check,
                "Agent console rejects a preview that differs from its capture receipt");
            File.WriteAllText(publishedPath, originalPreview);
            check(result.Status == "ReadyForReview" && result.AiProvider == "OpenRouter" && result.AiModel == "openai/gpt-5.5" &&
                result.AiInvoked && result.AiCalls == 1,
                "actual existing OpenRouter factory/non-streaming transport creates one controlled Reply Preview");
            check(first.Knowledge.IntentResults.Single().Provider == "JEV" &&
                result.Knowledge.QueryStatus == KnowledgeStatuses.Available && result.Knowledge.ProviderCalled,
                "new Agent Context→JEV→Product→Published Knowledge→AI→Preview chain completes");
            check(preview.SessionId == first.Knowledge.Session.SessionId &&
                preview.Input.MessageId == first.Knowledge.IntentResults[0].MessageId &&
                preview.Input.ProductId == ProductA && preview.Decision?.IntentName == "MATERIAL_QUERY" &&
                result.Knowledge.SellerId == "seller-A" && preview.CreatedAt != default,
                "Preview traces original Session/message/seller/product/Intent and generation time");
            check(preview.SendStatus == "Shadow" && preview.Input.Identity?.Status == BuyerIdentityStatuses.Observed &&
                first.Knowledge.Session.Identity.IdentityLevel == IdentityLevel.Observed &&
                preview.Confidence is null && preview.KnowledgeUsedByAi && !preview.KnowledgeGuardApplied,
                "AI output remains Observed Shadow with unknown confidence and no delivery state");
            var body = f.Body(preview);
            using var request = JsonDocument.Parse(body);
            var question = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            check(question.Contains("MATERIAL_QUERY") && question.Contains("Confidence: 1") && question.Contains("Provider: JEV") &&
                question.Contains("SellerId: seller-A") && question.Contains("ProductId: " + ProductA),
                "existing Prompt builder receives Intent/confidence/provider and current seller/product");
            check(question.Contains("A-TA1纯钛") && question.Contains("LocalTestProductConfig") &&
                question.Contains(f.Data.Published["A-Material"].KnowledgeId) &&
                question.Contains("fixture:v1350b/" + ProductA + "/Material") && !question.Contains("LEGACY"),
                "Prompt facts contain only current Published values with Knowledge ID/source/reference");
            check(request.RootElement.GetProperty("model").GetString() == "openai/gpt-5.5" &&
                request.RootElement.GetProperty("max_tokens").GetInt32() == 220 &&
                request.RootElement.GetProperty("temperature").GetDouble() == .15 &&
                !request.RootElement.GetProperty("stream").GetBoolean(),
                "Preview transport reuses unchanged Provider config/model/temperature/token budget");
            check(request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("不得编造"),
                "Preview system constraint forbids unsupported product facts and dialogue instructions");
            var repeat = await f.Process(JsonSerializer.Deserialize<MessageEnvelope>(JsonSerializer.Serialize(envelope))!, Ocr(ProductB));
            check(repeat.Results.Count == 0 && repeat.Knowledge.IntentResults.Count == 0 &&
                f.Pipeline.AiCalls == 1 && f.Pipeline.PreviewCount == 1 && f.Pipeline.KnowledgeCalls == 1,
                "serialized duplicate Envelope repeats neither Context/Intent/Knowledge nor AI/Preview");

            var assistant = new AssistantReplyService(f.State);
            var shown = assistant.Show(preview.ReplyPreviewId)!;
            check(assistant.List().Count == 1 && shown.SessionId == preview.SessionId &&
                shown.BuyerIdentityStatus == BuyerIdentityStatuses.Observed && shown.RiskLevel == "High",
                "existing assistant List/Show accepts original Agent IDs and shows Observed review risk");
            var edit = assistant.Edit(preview.ReplyPreviewId, "fixture-reviewer", "人工核实后修改的建议文本。");
            check(edit.Status == AssistedReplyStatuses.HumanEdited && edit.AiReply == preview.Reply &&
                edit.EffectiveReply == "人工核实后修改的建议文本。",
                "existing human edit retains immutable original Preview and changes only review sidecar");
            var approved = assistant.Approve(preview.ReplyPreviewId, "fixture-reviewer", edit.ReplySha256);
            var clipboard = new ClipboardProbe();
            var copied = assistant.Copy(preview.ReplyPreviewId, "fixture-reviewer", approved.ReplySha256, clipboard);
            check(copied.Status == AssistedReplyStatuses.Copied && clipboard.Calls == 1 &&
                clipboard.Text == edit.EffectiveReply, "existing approval/copy interface copies the exact reviewed text to a controlled clipboard probe");
            var feedback = assistant.RecordFeedback(preview.ReplyPreviewId, "fixture-reviewer", ReplyFeedbackTypes.Edited);
            check(feedback.Feedback.ReplyPreviewId == preview.ReplyPreviewId && feedback.Feedback.SessionId == preview.SessionId,
                "existing Feedback interface associates the newly generated Preview without Knowledge mutation or sending");

            f.Data.Handler.Choice = "PRODUCT_SPEC_QUERY";
            var second = One(await f.Process(Envelope("reply-A", "那200ml的呢", 2), Ocr(ProductA)));
            check(second.Preview is not null && second.Preview.Input.Messages.Count == 2 &&
                f.Body(second.Preview).Contains("这个酒壶是什么材质") && f.Body(second.Preview).Contains("A-200ml"),
                "continuous buyer Context and current Published Capacity reach the actual AI request");
            for (var i = 3; i <= 5; i++) await f.Process(Envelope("reply-A", "200ml规格-" + i, i), Ocr(ProductA));
            var latest = One(await f.Process(Envelope("reply-A", "最新200ml规格", 6), Ocr(ProductA)));
            check(latest.Preview!.Input.Messages.Count == 4 &&
                !f.Body(latest.Preview).Contains("这个酒壶是什么材质") &&
                latest.Preview.Input.Messages.Sum(message => message.Text.Length) <= 3000,
                "AI input uses the original current-plus-last-three bounded dialogue window");

            f.Data.Handler.Choice = "MATERIAL_QUERY";
            var b = One(await f.Process(Envelope("reply-B", "B独立材质问题", 7), Ocr(ProductB)));
            check(b.Preview!.SessionId != preview.SessionId && f.Body(b.Preview).Contains("B-304不锈钢") &&
                !f.Body(b.Preview).Contains("A-TA1纯钛") && !f.Body(b.Preview).Contains("最新200ml规格"),
                "Session B Prompt excludes A dialogue and Product A Knowledge");
            var otherSeller = One(await f.Process(Envelope("reply-other-seller", "同商品另店材质", 8, "seller-B"), Ocr(ProductA)));
            check(f.Body(otherSeller.Preview!).Contains("OTHER-玻璃") &&
                f.Body(otherSeller.Preview!).Contains("SellerId: seller-B") &&
                !f.Body(otherSeller.Preview!).Contains("A-TA1纯钛"),
                "same ProductId across sellers uses only the current seller's Published facts");

            var session = f.Pipeline.GetSession(preview.SessionId)!;
            Reject<InvalidDataException>(() => AgentReplyInputAdapter.Build(session,
                result.Knowledge with { ProductId = ProductB }, "request"), check, "Reply adapter rejects mismatched Knowledge/Product association");
            Reject<InvalidDataException>(() => AgentReplyInputAdapter.Build(session,
                result.Knowledge with { SellerId = "seller-B" }, "request"), check, "Reply adapter rejects a foreign seller");
            Reject<InvalidDataException>(() => AgentReplyInputAdapter.Build(session with { Status = AgentSessionStatus.Archived },
                result.Knowledge, "request"), check, "Reply adapter rejects Archived Context");
            var context = AgentReplyInputAdapter.Build(session, result.Knowledge, "request");
            Reject<InvalidDataException>(() => AgentReplyInputAdapter.Write(Path.Combine(f.Data.Root, "bad-facts.json"), context,
                result.Knowledge with { Knowledge = result.Knowledge.Knowledge! with { SellerId = "seller-B" } }),
                check, "existing Knowledge writer rejects foreign Published facts before Provider invocation");

            var callsBefore = f.Pipeline.AiCalls;
            f.Restart();
            check(f.Pipeline.AiCalls == 0 && f.Pipeline.PreviewCount == 0 &&
                new AssistantReplyService(f.State).Show(preview.ReplyPreviewId)!.EffectiveReply == edit.EffectiveReply,
                "component reconstruction preserves existing edited/copied Preview and does not generate historical replies");
            check((await f.Process(envelope, Ocr(ProductA))).Results.Count == 0 && f.Pipeline.AiCalls == 0,
                "SQLite-restored old messages never rerun AI or Preview");
            var restored = One(await f.Process(Envelope("reply-A", "恢复后材质新问题", 9), Ocr(ProductA)));
            check(restored.Preview?.SessionId == preview.SessionId && restored.Status == "ReadyForReview" && f.Pipeline.AiCalls == 1,
                "new message after Context recovery completes the full chain on the original Session");
            var upper = Envelope("reply-A", "大写角色材质", 10);
            upper = upper with { Messages = [upper.Messages[0] with { Role = "BUYER" }] };
            var upperResult = One(await f.Process(upper, Ocr(ProductA)));
            check(upperResult.Status == "ReadyForReview" &&
                upperResult.Preview!.Input.Messages[^1].Role == "buyer" &&
                upperResult.Knowledge.Intent.MessageId == upperResult.Preview.Input.MessageId,
                "Reply wire normalizes allowed role casing without changing original Stable MessageId");
            check(File.ReadAllText(f.Data.Workflow.ItemsPath) == workflowBefore,
                "AI/Preview/human operations do not modify Knowledge workflow or Published values");
            check(callsBefore > 0 && !Directory.EnumerateFiles(f.State, "*send*", SearchOption.AllDirectories).Any(),
                "complete path and human copy create no send request or delivery artifact");
        }
        using (var f = New("safe-knowledge-states"))
        {
            var missingProduct = One(await f.Process(Envelope("guard-buyer", "是什么材质", 1)));
            check(missingProduct.Preview is { KnowledgeGuardApplied: true, KnowledgeUsedByAi: false } &&
                missingProduct.Status == KnowledgeStatuses.MissingProductContext &&
                missingProduct.AiProvider == "SafetyGuard" && missingProduct.AiCalls == 0 &&
                missingProduct.Preview.Reply == KnowledgeReplyGuard.HumanCheckReply &&
                missingProduct.Preview.Input.ProductId == "",
                "No Product Context creates a deterministic human-check Preview without guessing or calling AI");
            var missing = One(await f.Process(Envelope("guard-buyer", "未发布商品材质", 2), Ocr(ProductC)));
            check(missing.Status == KnowledgeStatuses.Missing && missing.Preview!.KnowledgeGuardApplied &&
                !missing.Preview.Reply.Contains("LEGACY") && !missing.AiInvoked && f.Pipeline.AiCalls == 0,
                "Knowledge Missing cannot produce deterministic unknown facts or invoke AI");
            var workflow = File.ReadAllText(f.Data.Workflow.ItemsPath);
            File.WriteAllText(f.Data.Workflow.ItemsPath, "{");
            var unavailable = One(await f.Process(Envelope("guard-buyer", "知识源异常材质", 3), Ocr(ProductA)));
            check(unavailable.Status == AgentKnowledgePipeline.KnowledgeUnavailable &&
                unavailable.Knowledge.Error == "JsonException" && unavailable.Preview!.KnowledgeStatus == AgentKnowledgePipeline.KnowledgeUnavailable &&
                unavailable.Preview.Reply == KnowledgeReplyGuard.HumanCheckReply && f.Pipeline.AiCalls == 0,
                "KnowledgeUnavailable is retained in a safe Preview with no AI call");
            check(unavailable.Knowledge.Intent.SessionId == missingProduct.Preview!.SessionId &&
                new SQLiteAgentSessionStore(f.Data.Database).LoadSessions().Single().Messages.Count == 3,
                "Knowledge safe degradation preserves all three accepted Context messages");
            File.WriteAllText(f.Data.Workflow.ItemsPath, workflow);
            f.Data.Handler.Choice = "OTHER";
            var greeting = One(await f.Process(Envelope("guard-buyer", "你好", 4)));
            check(greeting.Status == "ReadyForReview" && greeting.Preview!.Input.ProductId == "" &&
                !greeting.Preview.KnowledgeUsedByAi && greeting.Preview.KnowledgeStatus == KnowledgeStatuses.NotRequired,
                "non-product OTHER intent can use the existing AI Provider with no product facts");
        }
        using (var f = New("provider-failures"))
        {
            var sequence = 1;
            foreach (var mode in new[] { "http", "timeout", "throw", "invalid", "empty", "no-key" })
            {
                f.Mode = mode;
                var envelope = Envelope("provider-buyer", "材质-" + mode, sequence++);
                var failed = One(await f.Process(envelope, Ocr(ProductA)));
                check(failed.Preview is null && failed.Status == "AIUnavailable" &&
                    failed.AiCalls == (mode == "no-key" ? 0 : 1), mode + " existing Provider failure creates no fake Preview");
                var before = f.Pipeline.AiCalls;
                check((await f.Process(envelope, Ocr(ProductA))).Results.Count == 0 && f.Pipeline.AiCalls == before,
                    mode + " failed reply attempt is not automatically repeated");
                check(new SQLiteAgentSessionStore(f.Data.Database).LoadSessions().Single().Messages.Count == sequence - 1,
                    mode + " AI failure leaves the accepted Context intact");
            }
            f.Mode = "success";
            f.Data.Handler.Fail = true;
            var fallback = One(await f.Process(Envelope("provider-buyer", "材质是什么", sequence++), Ocr(ProductA)));
            check(fallback.Status == "ReadyForReview" && fallback.Knowledge.Intent.Provider == "RuleBased" &&
                fallback.Knowledge.Intent.FallbackUsed && f.Body(fallback.Preview!).Contains("Provider: RuleBased"),
                "JEV failure→existing RuleBased→Knowledge→AI→Preview completes with correct Prompt provider metadata");
            f.Data.Handler.Fail = false;
            var oversized = One(await f.Process(Envelope("provider-buyer", "材质" + new string('长', 1001), sequence++), Ocr(ProductA)));
            check(oversized.Preview is null && !oversized.AiInvoked && oversized.Error == "InvalidDataException",
                "oversized current question is held before AI without truncating saved Context");
            f.Mode = "secret-echo";
            var redacted = One(await f.Process(Envelope("provider-buyer", "材质回执", sequence++), Ocr(ProductA)));
            check(redacted.Preview?.Reply == "[REDACTED]", "existing Provider redacts credential echoes before Preview and logs");
            var config = JsonNode.Parse(File.ReadAllText(f.Config))!.AsObject();
            config["Enabled"] = false; File.WriteAllText(f.Config, config.ToJsonString());
            f.Mode = "success";
            var disabled = One(await f.Process(Envelope("provider-buyer", "禁用模型材质", sequence++), Ocr(ProductA)));
            check(disabled.Preview is null && disabled.AiCalls == 0 && disabled.Status == "AIUnavailable",
                "existing Provider Enabled=false prevents HTTP without breaking saved Context");
            config["Enabled"] = true; File.WriteAllText(f.Config, config.ToJsonString());
            f.TamperSendReceipt = true;
            var rejectedReceipt = One(await f.Process(Envelope("provider-buyer", "回执校验材质", sequence++), Ocr(ProductA)));
            check(rejectedReceipt.Preview is null && rejectedReceipt.Error == "InvalidDataException",
                "a synthetically tampered SendEnabled receipt is rejected before Preview publication; no send is invoked");
        }
        using (var f = New("concurrent"))
        {
            var envelope = Envelope("concurrent-reply", "材质", 1);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                f.Pipeline.ProcessMessageAsync(envelope, Ocr(ProductA), "concurrent-capture")));
            f.Results.AddRange(results.SelectMany(r => r.Results));
            check(results.Sum(r => r.Results.Count) == 1 && f.Pipeline.PreviewCount == 1 && f.Pipeline.AiCalls == 1 &&
                f.Pipeline.KnowledgeCalls == 1 && f.Data.Handler.Calls == 1,
                "eight concurrent copies execute one Intent, Knowledge, AI call and Preview");
            f.Pipeline.Dispose();
            try { await f.Pipeline.ProcessMessageAsync(envelope); check(false, "disposed"); }
            catch (ObjectDisposedException) { check(true, "disposed Reply pipeline rejects new execution"); }
        }
        var all = fixtures.SelectMany(f => f.Results).ToArray();
        var previews = all.Where(result => result.Preview is not null).ToArray();
        check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".json", ".jsonl", ".log", ".txt" }.Contains(Path.GetExtension(path)))
            .Any(path => File.ReadAllText(path).Contains(TestKeyMarker)), "controlled secret audit finds no API key/header/exception-message leak");
        check(!Directory.EnumerateFiles(root, "*send-request*", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(root, "reply-delivery.json", SearchOption.AllDirectories).Any(),
            "all normal/fallback/AI failure/review paths keep Send artifacts and SentMessages at zero");
        File.WriteAllText(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new
        {
            Version = "1.35.0-C", EvidenceKind = "ControlledHTTPExistingOpenRouterProvider",
            IntentHTTPCalls = fixtures.Sum(f => f.Data.Handler.Calls), AiHTTPCalls = all.Sum(result => result.AiCalls),
            ReplyPreviewCount = previews.Length, ProviderPreviewCount = previews.Count(result => result.AiInvoked),
            SafetyPreviewCount = previews.Count(result => !result.AiInvoked),
            AiUnavailable = all.Count(result => result.Status == "AIUnavailable"),
            Identity = "Observed", AutomaticSend = 0, SentMessages = 0, RealMultiBuyer = "Pending",
            ExternalAIReal = "NotRun", HumanOperations = "Existing Show/Edit/Approve/Copy/Feedback interfaces",
            SecretPresent = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("V1350C_CONTROLLED_EVIDENCE: " + root);
    }
}
