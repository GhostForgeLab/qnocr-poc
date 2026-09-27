using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class IntentProviderFactoryChecks
{
    private sealed class Handler(HttpStatusCode status, string choice) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
            var names = new[] { "PRODUCT_SPEC_QUERY", "MATERIAL_QUERY", "SHIPPING_QUERY",
                "CUSTOMIZATION_QUERY", "PRICE_QUERY", "AFTER_SALES_QUERY", "GIFT_QUERY",
                "COLLECTION_QUERY", "OTHER" };
            var probabilities = names.ToDictionary(name => name, name => name == choice ? 1.0 : 0.0);
            var payload = JsonSerializer.Serialize(new { model = "jev-test-1", answers = new
            { intent = new { type = "choice", choice, confidence = 1.0, probabilities } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV127", "factory_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var configPath = Path.GetFullPath(Path.Combine("Config", "intent-provider.json"));
        var config = IntentProviderConfig.Load(configPath);
        check(config.Provider == "JEV" && config.Fallback == "RuleBased" && config.Enabled &&
            config.JevConfigPath == "jev-provider.runtime.json" &&
            JEVProviderConfig.Load(Path.Combine("Config", config.JevConfigPath)).Enabled,
            "runtime config selects enabled JEV with RuleBased fallback without changing PoC config");
        var context = Context("是TA1纯钛做的吗？", new string('1', 64));
        var observed = new List<IntentProviderTrace>();
        var successHandler = new Handler(HttpStatusCode.OK, "MATERIAL_QUERY");
        using (var selected = IntentClassifierFactory.Create(configPath, observed.Add,
            new HttpClient(successHandler), () => "test-key"))
        {
            var result = selected.Classifier.Classify(context);
            check(selected.SelectedProvider == "JEV" && !selected.FallbackUsed &&
                result.IntentName == "MATERIAL_QUERY" && result.RuleVersion == "JEV/jev-test-1" &&
                successHandler.Calls == 1,
                "JEV config selects JEV and uses its Choice result for one message");
            check(observed.Any(item => item.Event == "INTENT_PROVIDER_SELECTED" && item.Provider == "JEV") &&
                observed.Any(item => item.Event == "INTENT_PROVIDER_USED" && item.Provider == "JEV" &&
                    item.Intent == "MATERIAL_QUERY" && !item.FallbackUsed && item.SessionId == context.SessionId),
                "selected and per-message JEV usage trace records provider, intent, fallback flag and identity");
        }
        var rulePath = Path.Combine(root, "rule.json");
        File.WriteAllText(rulePath, JsonSerializer.Serialize(config with { Provider = "RuleBased" }));
        var ruleHandler = new Handler(HttpStatusCode.OK, "MATERIAL_QUERY");
        using (var selected = IntentClassifierFactory.Create(rulePath, null,
            new HttpClient(ruleHandler), () => "test-key"))
            check(selected.SelectedProvider == "RuleBased" && !selected.FallbackUsed &&
                selected.Classifier.Classify(context).IntentName == "OTHER" && ruleHandler.Calls == 0,
                "RuleBased config never calls JEV and preserves original rule result");
        observed.Clear();
        var missingKeyHandler = new Handler(HttpStatusCode.OK, "MATERIAL_QUERY");
        using (var selected = IntentClassifierFactory.Create(configPath, observed.Add,
            new HttpClient(missingKeyHandler), () => null))
        {
            var fallback = selected.Classifier.Classify(context);
            check(selected.SelectedProvider == "RuleBased" && selected.FallbackUsed &&
                fallback.IntentName == "OTHER" && missingKeyHandler.Calls == 0 &&
                observed.Any(item => item.Event == "JEV_PROVIDER_UNAVAILABLE" && item.Reason == "ApiKeyMissing") &&
                observed.Any(item => item.Event == "INTENT_PROVIDER_USED" && item.Provider == "RuleBased" && item.FallbackUsed),
                "missing key is detected before startup request and safely selects RuleBased");
        }
        observed.Clear();
        var failedHandler = new Handler(HttpStatusCode.ServiceUnavailable, "MATERIAL_QUERY");
        using (var selected = IntentClassifierFactory.Create(configPath, observed.Add,
            new HttpClient(failedHandler), () => "test-key"))
        {
            var fallback = selected.Classifier.Classify(context);
            check(fallback.IntentName == "OTHER" && failedHandler.Calls == 1 &&
                observed.Any(item => item.Event == "JEV_PROVIDER_FAILED" && item.FallbackUsed) &&
                observed.Any(item => item.Event == "INTENT_PROVIDER_USED" && item.Provider == "RuleBased" &&
                    item.FallbackUsed && item.Intent == "OTHER"),
                "per-message JEV HTTP failure falls back to RuleBased without losing the message");
        }
        var disabledPath = Path.Combine(root, "disabled.json");
        File.WriteAllText(disabledPath, JsonSerializer.Serialize(config with { Enabled = false }));
        using (var selected = IntentClassifierFactory.Create(disabledPath, null, null, () => null))
            check(selected.SelectedProvider == "RuleBased" && !selected.FallbackUsed &&
                selected.Classifier.Classify(context).IntentName == "OTHER",
                "disabled provider selects RuleBased without needing a key");
        var invalidPath = Path.Combine(root, "unavailable.json");
        File.WriteAllText(invalidPath, JsonSerializer.Serialize(config with { JevConfigPath = "missing.json" }));
        observed.Clear();
        using (var selected = IntentClassifierFactory.Create(invalidPath, observed.Add, null, () => "test-key"))
            check(selected.SelectedProvider == "RuleBased" && selected.FallbackUsed &&
                observed.Any(item => item.Event == "JEV_PROVIDER_UNAVAILABLE"),
                "missing JEV endpoint config safely degrades before dispatch");
        await Dispatch("jev", configPath, root, new Handler(HttpStatusCode.OK, "MATERIAL_QUERY"),
            "MATERIAL_QUERY", false, check);
        await Dispatch("fallback", configPath, root, new Handler(HttpStatusCode.ServiceUnavailable, "MATERIAL_QUERY"),
            "OTHER", true, check);
        check(!Directory.EnumerateFiles(root, "send-request.json", SearchOption.AllDirectories).Any(),
            "JEV/default and fallback Shadow paths create no send request");
    }

    private static SessionContextView Context(string question, string messageId) =>
        new(new string('A', 64), "buyer-test", "",
            [new EnvelopeMessage("buyer", question, new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.UtcNow, "intent-provider-fixture:capture", messageId, new string('C', 64), 1);

    private static async Task Dispatch(string name, string configPath, string root, Handler handler,
        string expectedIntent, bool expectedFallback, Action<bool, string> check)
    {
        var context = Context("是TA1纯钛做的吗？", name == "jev" ? new string('2', 64) : new string('3', 64));
        var run = Path.Combine(root, name + "-run"); var state = Path.Combine(root, name + "-state");
        var trace = new AgentPipelineTrace(run);
        using var selection = IntentClassifierFactory.Create(configPath, trace.IntentProviderObserved,
            new HttpClient(handler), () => "test-key");
        var session = new MessageSession(context.SessionId, "seller-test", "buyer-test", "fixture", "Test", "", "test-mapping.json");
        var item = new DispatchItem(context.MessageId, new string('D', 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, "", [context.Messages[^1]]),
            DateTimeOffset.UtcNow, true, CaptureId: context.CaptureId, AiRequestId: context.AiRequestId);
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(state, Path.Combine(run, "lifecycle.jsonl"),
            CancellationToken.None, (_, _, _, _) =>
            {
                aiCalls++;
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "仅供测试的回复预览。", 20, DateTimeOffset.UtcNow, 1));
            }, true, trace, intentClassifier: selection.Classifier))
        {
            dispatch.Submit(item, context); dispatch.Start(); await dispatch.StopAsync();
        }
        var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(state,
            "outbox", item.MessageId, "reply-preview.json")))!;
        check(aiCalls == 1 && preview.Decision?.IntentName == expectedIntent &&
            preview.SendStatus == "Shadow" && preview.Reply.Length > 0,
            name + " classifier reaches the unchanged AI callback and creates one Shadow preview");
        var traceText = File.ReadAllText(Path.Combine(run, "agent-pipeline.jsonl"));
        var results = File.ReadLines(Path.Combine(run, "agent-pipeline.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.GetProperty("Event").GetString() == "INTENT_PROVIDER_RESULT").ToArray();
        check(results.Length == 1 && results[0].GetProperty("Provider").GetString() ==
            (expectedFallback ? "RuleBased" : "JEV") &&
            results[0].GetProperty("Intent").GetString() == expectedIntent &&
            results[0].GetProperty("FallbackUsed").GetBoolean() == expectedFallback &&
            results[0].GetProperty("IntentLatencyMilliseconds").GetDouble() >= 0,
            name + " result trace records session, provider, intent, latency and fallback for one message");
        check(traceText.Contains("INTENT_PROVIDER_USED") &&
            traceText.Contains("\"FallbackUsed\":" + expectedFallback.ToString().ToLowerInvariant()) &&
            traceText.Contains("AI_RESPONSE") && traceText.Contains("REPLY_PREVIEW_CREATED"),
            name + " trace links provider use to AI response and preview without send");
    }
}
