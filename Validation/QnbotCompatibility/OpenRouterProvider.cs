using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CompatibilityHost
{
    internal sealed class OpenRouterProvider : IAIProvider
    {
        private const string AllowedEndpoint = "https://openrouter.ai/api/v1";
        private readonly AiShadowRuntime runtime;
        public AIProviderConfig Config { get; private set; }

        public OpenRouterProvider(AIProviderConfig config, AiShadowRuntime runtime)
        {
            Config = config ?? throw new ArgumentNullException("config");
            this.runtime = runtime ?? throw new ArgumentNullException("runtime");
            if (!string.Equals((config.Endpoint ?? "").TrimEnd('/'), AllowedEndpoint, StringComparison.Ordinal))
                throw new InvalidOperationException("OpenRouter provider requires its HTTPS API endpoint.");
        }

        public void PrepareAgentPipeline()
        {
            if (!Config.Enabled) throw new InvalidOperationException("AI provider is disabled.");
            // The unmodified qnbot generation service fixes these two values internally.
            // Reject unsupported overrides rather than pretending the file changed the model request.
            if (Config.Temperature != 0.15 || Config.MaxTokens != 220)
                throw new NotSupportedException("The current qnbot generation path fixes temperature=0.15 and max_tokens=220.");
            runtime.VerifyOpenRouterModel(Config.Model);
        }

        public Task<string> GenerateAsync(string seller, string buyer, string question, CancellationToken token)
        {
            PrepareAgentPipeline();
            // Keep the original qnbot prompt/routing/response parsing untouched.
            return runtime.GenerateAsync(seller, buyer, question, token);
        }

        public async Task<ProviderSmokeResult> SmokeAsync(string question)
        {
            if (!Config.Enabled) throw new InvalidOperationException("AI provider is disabled.");
            var key = runtime.GetStoredOpenRouterKey();
            var json = new JavaScriptSerializer();
            var payload = json.Serialize(new { model = Config.Model,
                messages = new[] { new { role = "user", content = question } },
                max_tokens = Config.MaxTokens, temperature = Config.Temperature, stream = false });
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, AllowedEndpoint + "/chat/completions"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    string content = null;
                    if ((int)response.StatusCode == 200)
                    {
                        var result = json.Deserialize<Dictionary<string, object>>(body);
                        object choices;
                        if (result.TryGetValue("choices", out choices))
                        {
                            var first = ((IEnumerable)choices).Cast<Dictionary<string, object>>().FirstOrDefault();
                            if (first != null)
                            {
                                var message = (Dictionary<string, object>)first["message"];
                                object text;
                                if (message.TryGetValue("content", out text)) content = text as string;
                            }
                        }
                    }
                    return new ProviderSmokeResult { ProviderName = Config.ProviderName, Endpoint = AllowedEndpoint,
                        Model = Config.Model, Temperature = Config.Temperature, MaxTokens = Config.MaxTokens,
                        HttpStatus = (int)response.StatusCode,
                        Content = content == null ? null : content.Replace(key, "[REDACTED]"),
                        Success = (int)response.StatusCode == 200 && !string.IsNullOrWhiteSpace(content) };
                }
            }
        }
    }
}
