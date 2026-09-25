using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CompatibilityHost
{
    internal sealed partial class AiShadowRuntime
    {
        private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

        private static void RequireOpenRouter(string url)
        {
            if (!string.Equals((url ?? "").TrimEnd('/'), OpenRouterBaseUrl, StringComparison.Ordinal))
                throw new InvalidOperationException("AI Shadow requires https://openrouter.ai/api/v1.");
        }

        public static object ConfigureOpenRouter(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || !model.Contains("/") || model.Any(char.IsWhiteSpace))
                throw new ArgumentException("Supply an OpenRouter model ID, e.g. openai/gpt-5.5.");
            // Snapshot before loading Bot: its SQLite connection keeps params.db open.
            var root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../.tools/ai-shadow"));
            var backup = Path.Combine(root, "provider-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
            foreach (var relative in new[] { "data/params.db", "shops/qnocr_ai_shadow_test/config/settings.json" })
            {
                var source = Path.Combine(root, "data-root", relative);
                if (File.Exists(source + "-wal")) throw new InvalidOperationException("Obtain a consistent settings backup before switching providers.");
                var target = Path.Combine(backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, false);
                if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(target)))
                    throw new IOException("Provider settings backup verification failed.");
            }
            return new AiShadowRuntime().SaveOpenRouter(model, backup);
        }

        private object SaveOpenRouter(string model, string backup)
        {
            var endpoints = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEndpoints")).Cast<object>().ToArray();
            var primary = endpoints.First(e => (bool)e.GetType().GetProperty("Enabled").GetValue(e, null));
            var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                var local = endpoints.FirstOrDefault(e =>
                    string.Equals((string)e.GetType().GetProperty("BaseUrl").GetValue(e, null), OpenRouterBaseUrl, StringComparison.Ordinal));
                if (local != null) key = (string)local.GetType().GetProperty("ApiKey").GetValue(local, null);
            }
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Set OPENROUTER_API_KEY in the host environment, or configure a local OpenRouter key first.");

            // Clone existing settings to preserve the endpoint prompt and all generation options.
            var replacement = primary.GetType().GetMethod("Clone").Invoke(primary, null);
            Action<string, object> set = (name, value) => replacement.GetType().GetProperty(name).SetValue(replacement, value, null);
            set("Id", "qnocr-openrouter");
            set("Name", "OpenRouter");
            set("BaseUrl", OpenRouterBaseUrl);
            set("ApiKey", key.Trim());
            set("Model", model);
            set("TextModel", model);
            set("Enabled", true);
            var json = new JavaScriptSerializer();
            // SaveEndpoints would also copy SystemPrompt to legacy settings. Save only the provider list.
            Call("Bot.ChromeNs.AiEndpointStore", "SaveEndpointsJson", json.Serialize(new[] { replacement }));
            var saved = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().Single();
            RequireOpenRouter((string)saved.GetType().GetProperty("BaseUrl").GetValue(saved, null));
            if ((string)saved.GetType().GetProperty("TextModel").GetValue(saved, null) != model
                || (string)saved.GetType().GetProperty("ApiKey").GetValue(saved, null) != key.Trim())
                throw new IOException("Provider configuration read-back failed.");
            return new { Provider = "OpenRouter", BaseUrl = OpenRouterBaseUrl, Model = model,
                Backup = backup, ApiKeyPrinted = false, AiEnabled = true, SendEnabled = false };
        }

        // Standalone synthetic request: no business prompt, customer data, adapter or delivery calls.
        public async Task<int> TestProviderAsync()
        {
            var endpoint = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().Single();
            Func<string, string> get = name => (string)endpoint.GetType().GetProperty(name).GetValue(endpoint, null);
            RequireOpenRouter(get("BaseUrl"));
            var json = new JavaScriptSerializer();
            var payload = json.Serialize(new { model = get("TextModel"),
                messages = new[] { new { role = "user", content = "Reply with exactly OPENROUTER_OK." } },
                max_tokens = 220, temperature = 0.15, stream = false });
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, OpenRouterBaseUrl + "/chat/completions"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", get("ApiKey"));
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
                    var success = (int)response.StatusCode == 200 && !string.IsNullOrWhiteSpace(content);
                    Console.WriteLine(json.Serialize(new { Mode = "AiProviderSmoke", BaseUrl = OpenRouterBaseUrl,
                        Model = get("TextModel"), HttpStatus = (int)response.StatusCode, Success = success,
                        Content = content == null ? null : content.Replace(get("ApiKey"), "[REDACTED]"),
                        AiEnabled = true, SendEnabled = false, SendEntryInvoked = false, SentMessages = 0 }));
                    return success ? 0 : 3;
                }
            }
        }
    }
}
