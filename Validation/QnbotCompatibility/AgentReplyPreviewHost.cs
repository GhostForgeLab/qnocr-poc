using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using QnocrPoc.Integration.Wire;

namespace CompatibilityHost
{
    internal static class AgentReplyPreviewHost
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private const string TestKey = "CONTROLLED_V1350C_KEY_NEVER_LOG";
        private sealed class ControlledHandler : HttpMessageHandler
        {
            public string Mode, Body;
            public int Calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                Body = await request.Content.ReadAsStringAsync();
                if (Mode == "throw") throw new IOException(TestKey);
                if (Mode == "timeout") throw new TaskCanceledException(TestKey);
                if (Mode == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (Mode == "invalid") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") };
                var text = Mode == "empty" ? " " : Mode == "secret-echo" ? TestKey : "受控AI建议：请根据当前已发布资料核对商品信息。";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Json.Serialize(new { choices = new[] { new { message = new { content = text } } } }),
                        Encoding.UTF8, "application/json")
                };
            }
        }

        public static async Task<int> RunAsync(string input, string configPath, string output, string controlledMode = null)
        {
            var context = Json.Deserialize<AgentContextWire>(File.ReadAllText(input));
            Validate(context);
            var config = AIProviderConfig.Load(configPath);
            if (controlledMode != null && !new[] { "success", "http", "throw", "timeout", "invalid", "empty", "no-key", "secret-echo" }.Contains(controlledMode))
                throw new ArgumentException("Unknown controlled Provider scenario.");
            Directory.CreateDirectory(output);
            var question = Program.BuildModelQuestion(context.Messages.Last().Text, context);
            if (question.Length > 12000) throw new InvalidDataException("Agent model question exceeds its budget.");
            var handler = controlledMode == null ? null : new ControlledHandler { Mode = controlledMode };
            using (var client = handler == null ? null : new HttpClient(handler))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                IAIProvider provider = AIProviderFactory.Create(config,
                    handler == null ? new AiShadowRuntime() : null, client,
                    handler == null ? (Func<string>)null : () => controlledMode == "no-key" ? null : TestKey);
                ProviderSmokeResult answer = null;
                string error = null;
                try { answer = await provider.PreviewAsync(question, deadline.Token); }
                catch (Exception ex) { error = ex.GetType().Name; } // No exception messages, headers or keys.
                if (handler != null && handler.Body != null)
                    File.WriteAllText(Path.Combine(output, "controlled-provider-request.json"), handler.Body);
                var calls = provider.PreviewHttpCalls;
                var success = error == null && answer != null && answer.Success;
                var receipt = new
                {
                    Mode = "AgentReplyPreviewOnly", EvidenceKind = handler == null ? "ExternalProvider" : "ControlledHTTPExistingProvider",
                    Status = success ? "Converted" : answer != null && answer.HttpStatus == 200 ? "EmptyReply" : "AiFailed",
                    Reason = error ?? (success ? null : "ProviderUnavailable"),
                    context.SessionId, context.MessageId, context.AiRequestId, context.SellerId, context.ProductId,
                    ProviderName = config.ProviderName, ProviderModel = config.Model,
                    AiInvoked = calls > 0, AiCalls = calls, ConvertedMessages = success ? 1 : 0,
                    ReplySuggestion = success ? answer.Content : null,
                    PromptLength = question.Length, AiRequestAt = DateTimeOffset.UtcNow.ToString("O"),
                    SendEnabled = false, SendEntryInvoked = false, SentMessages = 0,
                    CoordinatorCreated = false, ForbiddenBusinessCalls = Guard.ForbiddenCalls, RuntimeInitializers = 0
                };
                File.WriteAllText(Path.Combine(output, "shadow-adapter-result.json"), Json.Serialize(receipt));
                Console.WriteLine(Json.Serialize(receipt));
                return success ? 0 : 3;
            }
        }

        private static void Validate(AgentContextWire context)
        {
            Guid id;
            if (context == null || !Guid.TryParseExact(context.SessionId, "N", out id) ||
                context.SessionStatus != "Active" || string.IsNullOrWhiteSpace(context.SellerId) ||
                string.IsNullOrWhiteSpace(context.BuyerId) || string.IsNullOrWhiteSpace(context.MessageId) ||
                string.IsNullOrWhiteSpace(context.AiRequestId) || string.IsNullOrWhiteSpace(context.IntentName) ||
                string.IsNullOrWhiteSpace(context.IntentProvider) || context.IntentConfidence < 0 || context.IntentConfidence > 1 ||
                context.Identity == null || context.Identity.Status != "Observed" ||
                context.Identity.SessionId != context.SessionId || context.Identity.BuyerId != context.BuyerId ||
                context.Messages == null || context.Messages.Count < 1 || context.Messages.Count > 4 ||
                context.ModelInputMessageCount != context.Messages.Count ||
                context.Messages.Any(m => m == null || string.IsNullOrWhiteSpace(m.Text) ||
                    !(m.Role == "buyer" || m.Role == "seller")) ||
                context.Messages.Sum(m => m.Text.Length) > 3000 ||
                context.Messages.Last().Role != "buyer" || context.Messages.Last().Text.Length > 1000)
                throw new InvalidDataException("Agent Preview input is not bounded Observed current-session context.");
            if (context.Knowledge != null)
            {
                if (context.Knowledge.Status != "Available" || context.ProductContextBindingStatus != "Bound" ||
                    context.CurrentProductContext == null || context.CurrentProductContext.SessionId != context.SessionId ||
                    context.CurrentProductContext.SellerId != context.SellerId ||
                    context.CurrentProductContext.ProductId != context.ProductId ||
                    string.IsNullOrWhiteSpace(context.KnowledgeId) || string.IsNullOrWhiteSpace(context.KnowledgeReference))
                    throw new InvalidDataException("Preview facts require current scoped Published provenance.");
                Program.ValidateKnowledgeContext(context, new MessageEnvelope
                { SellerId = context.SellerId, BuyerId = context.BuyerId, ProductId = context.ProductId });
            }
            else if (context.IntentName != "OTHER" || !string.IsNullOrEmpty(context.ProductId))
                throw new InvalidDataException("Product questions cannot reach AI without Published knowledge.");
        }
    }
}
