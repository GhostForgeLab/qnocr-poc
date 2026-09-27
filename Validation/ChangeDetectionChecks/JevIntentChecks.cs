using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QnocrPoc.Decision;
using QnocrPoc.Models;

internal static class JevIntentChecks
{
    private sealed class ChoiceHandler(string choice = "PRODUCT_SPEC_QUERY") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? HeaderScheme { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            HeaderScheme = request.Headers.Authorization?.Scheme;
            var names = new[] { "PRODUCT_SPEC_QUERY", "MATERIAL_QUERY", "SHIPPING_QUERY",
                "CUSTOMIZATION_QUERY", "PRICE_QUERY", "AFTER_SALES_QUERY", "GIFT_QUERY",
                "COLLECTION_QUERY", "OTHER" };
            var probabilities = names.ToDictionary(name => name, name => name == choice ? 1.0 : 0.0);
            var payload = JsonSerializer.Serialize(new { model = "jev-test-1", answers = new
            {
                intent = new { type = "choice", choice, confidence = 1.0, probabilities }
            } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class ExpectedClassifier(IReadOnlyDictionary<string, string> expected) : IIntentClassifier
    {
        public IntentResult Classify(SessionContextView context)
        {
            var message = context.Messages[^1].Text;
            return new IntentResult(expected[message], 0.8, true, true, true, "Medium", "JEV/mock", "choice");
        }
    }

    public static void Run(Action<bool, string> check)
    {
        var config = JEVProviderConfig.Load(Path.Combine("Config", "jev-provider.json"));
        check(!config.Enabled && config.Model == "jev-latest" && config.TimeoutSeconds == 20 &&
            config.Endpoint == "https://api.typesafe.ai/v1/systemone",
            "JEV endpoint/model/timeout are loaded from a default-disabled config file");
        var context = new SessionContextView(new string('A', 64), "buyer-test", "",
            [new EnvelopeMessage("buyer", "这款有200ml规格吗？", new DateTime(2026, 9, 27, 12, 0, 0))],
            DateTimeOffset.Now, "jev-fixture:capture", new string('1', 64), new string('C', 64), 1);
        var blockedHandler = new ChoiceHandler();
        using (var disabled = new JEVIntentClassifier(config, new HttpClient(blockedHandler), () => "test-key"))
        {
            try { disabled.Classify(context); throw new Exception("disabled JEV called"); }
            catch (InvalidOperationException) { check(blockedHandler.Calls == 0,
                "default-disabled JEV makes no HTTP call and cannot replace RuleBased"); }
        }
        var enabled = config with { Enabled = true, Model = "jev-test" };
        var handler = new ChoiceHandler();
        using (var classifier = new JEVIntentClassifier(enabled, new HttpClient(handler), () => "test-key"))
        {
            IIntentClassifier intent = classifier;
            var result = intent.Classify(context);
            check(result.IntentName == "PRODUCT_SPEC_QUERY" && result.Confidence == 1 &&
                result.NeedProductContext && result.NeedHumanReview && result.RuleVersion == "JEV/jev-test-1",
                "JEV implements the existing IIntentClassifier and parses a typed Choice answer");
            using var request = JsonDocument.Parse(handler.Body!);
            var root = request.RootElement;
            check(handler.Calls == 1 && handler.HeaderScheme == "Bearer" &&
                root.GetProperty("model").GetString() == "jev-test" &&
                root.GetProperty("state").GetString() == context.Messages[^1].Text &&
                root.GetProperty("questions").GetProperty("intent").GetProperty("criteria")
                    .EnumerateObject().Count() == 9 && !handler.Body!.Contains("test-key"),
                "JEV request sends only the synthetic buyer question, model and 9-option Choice; key stays in header");
        }
        using (var bad = new JEVIntentClassifier(enabled, new HttpClient(new ChoiceHandler("UNEXPECTED")),
            () => "test-key"))
        {
            try { bad.Classify(context); throw new Exception("invalid JEV output accepted"); }
            catch (InvalidDataException) { check(true, "unknown JEV choice is rejected before comparison"); }
        }
        var samples = JsonSerializer.Deserialize<List<IntentSample>>(File.ReadAllText(
            Path.Combine("Decision", "intent-samples.v126.json")))!;
        check(samples.Count == 20 && samples.Select(item => item.Id).Distinct().Count() == 20 &&
            new[] { "PRODUCT_SPEC_QUERY", "MATERIAL_QUERY", "SHIPPING_QUERY", "AFTER_SALES_QUERY",
                "CUSTOMIZATION_QUERY", "PRICE_QUERY", "GIFT_QUERY", "COLLECTION_QUERY", "OTHER" }
                .All(label => samples.Any(sample => sample.ExpectedIntent == label)),
            "20 synthetic questions cover specification, material, shipping, after-sales, customization, price, gift, collection and vague intents");
        var output = Path.GetFullPath(Path.Combine("Output", "ValidationV126", "comparison_" + Guid.NewGuid().ToString("N")));
        var expected = samples.ToDictionary(sample => sample.Message, sample => sample.ExpectedIntent);
        var compared = new IntentComparisonRunner(new RuleBasedIntentClassifier(),
            new ExpectedClassifier(expected)).Compare(samples, output);
        check(compared.Count == 20 && compared.Any(row => row.Difference == "Different") &&
            compared.All(row => row.JevIntent == row.ExpectedIntent) &&
            compared.All(row => row.JevModel == "mock"),
            "comparison runner retains both results and marks rule/model differences without selecting either");
        try { new IntentComparisonRunner(new RuleBasedIntentClassifier(),
            new ExpectedClassifier(expected)).Compare(samples, output);
            throw new Exception("comparison log overwritten"); }
        catch (InvalidOperationException) { check(true, "a repeat comparison cannot overwrite or append to an existing audit run"); }
        var lines = File.ReadAllLines(Path.Combine(output, "intent-comparison.jsonl"));
        check(lines.Length == 20 && lines.All(line => line.Contains("INTENT_COMPARISON") &&
            line.Contains("RuleResult") && line.Contains("JEVResult") && line.Contains("Timestamp")) &&
            !File.ReadAllText(Path.Combine(output, "intent-comparison.jsonl")).Contains("test-key"),
            "20 INTENT_COMPARISON audit records include input/results/time and no API key");
        check(!Directory.EnumerateFiles(output, "send-request.json", SearchOption.AllDirectories).Any(),
            "comparison-only run creates no reply or send request");
    }
}
