using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                throw new ArgumentException("Supply a nonempty OpenRouter model ID.");
            // Snapshot before loading Bot: its SQLite connection keeps params.db open.
            var backup = BackupProviderSettings();
            return new AiShadowRuntime().SaveOpenRouter(model, backup);
        }

        internal static string BackupProviderSettings()
        {
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
            return backup;
        }

        public string GetStoredOpenRouterKey()
        {
            var endpoints = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().ToArray();
            if (endpoints.Length != 1) throw new InvalidOperationException("Exactly one enabled AI endpoint is required.");
            var endpoint = endpoints[0];
            RequireOpenRouter((string)endpoint.GetType().GetProperty("BaseUrl").GetValue(endpoint, null));
            var key = (string)endpoint.GetType().GetProperty("ApiKey").GetValue(endpoint, null);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Configure an OpenRouter key in the isolated qnbot settings first.");
            return key;
        }

        public void VerifyOpenRouterModel(string model)
        {
            var endpoints = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().ToArray();
            if (endpoints.Length != 1) throw new InvalidOperationException("Exactly one enabled AI endpoint is required.");
            var active = endpoints[0];
            RequireOpenRouter((string)active.GetType().GetProperty("BaseUrl").GetValue(active, null));
            GetStoredOpenRouterKey();
            if ((string)active.GetType().GetProperty("TextModel").GetValue(active, null) != model)
                throw new InvalidOperationException("Configured model differs from isolated qnbot endpoint; apply provider config before AI Shadow.");
        }

        public void ApplyOpenRouterModel(string model)
        {
            var endpoints = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().ToArray();
            if (endpoints.Length != 1) throw new InvalidOperationException("Exactly one enabled AI endpoint is required.");
            var active = endpoints[0];
            RequireOpenRouter((string)active.GetType().GetProperty("BaseUrl").GetValue(active, null));
            GetStoredOpenRouterKey();
            if ((string)active.GetType().GetProperty("TextModel").GetValue(active, null) == model) return;
            var replacement = active.GetType().GetMethod("Clone").Invoke(active, null);
            replacement.GetType().GetProperty("Model").SetValue(replacement, model, null);
            replacement.GetType().GetProperty("TextModel").SetValue(replacement, model, null);
            Call("Bot.ChromeNs.AiEndpointStore", "SaveEndpointsJson", new JavaScriptSerializer().Serialize(new[] { replacement }));
            var saved = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().Single();
            if ((string)saved.GetType().GetProperty("TextModel").GetValue(saved, null) != model)
                throw new IOException("Isolated provider model read-back failed.");
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

    }
}
