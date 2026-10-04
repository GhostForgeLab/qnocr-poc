using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace CompatibilityHost
{
    internal sealed class AIProviderConfig
    {
        public string ProviderName { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
        public bool Enabled { get; set; }

        public static AIProviderConfig Load(string path)
        {
            var json = File.ReadAllText(Path.GetFullPath(path));
            var serializer = new JavaScriptSerializer();
            var fields = serializer.Deserialize<Dictionary<string, object>>(json);
            var allowed = new[] { "ProviderName", "Endpoint", "Model", "Temperature", "MaxTokens", "Enabled" };
            if (fields == null || fields.Count != allowed.Length || allowed.Any(k => !fields.ContainsKey(k)))
                throw new InvalidDataException("Provider config contains an unknown or credential field.");
            var result = serializer.Deserialize<AIProviderConfig>(json);
            if (result == null || string.IsNullOrWhiteSpace(result.ProviderName)
                || string.IsNullOrWhiteSpace(result.Endpoint) || string.IsNullOrWhiteSpace(result.Model)
                || result.Model.Any(char.IsWhiteSpace) || !result.Model.Contains("/")
                || double.IsNaN(result.Temperature) || double.IsInfinity(result.Temperature)
                || result.Temperature < 0 || result.Temperature > 2 || result.MaxTokens < 1 || result.MaxTokens > 8192)
                throw new InvalidDataException("Invalid non-secret AI provider configuration.");
            if (string.Equals(result.ProviderName, "OpenRouter", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.Endpoint.TrimEnd('/'), "https://openrouter.ai/api/v1", StringComparison.Ordinal))
                throw new InvalidDataException("OpenRouter provider endpoint must be its HTTPS API URL.");
            return result;
        }
    }

    internal static class AIProviderFactory
    {
        public static IAIProvider Create(AIProviderConfig config, AiShadowRuntime runtime,
            System.Net.Http.HttpClient previewClient = null, Func<string> previewKey = null)
        {
            if (string.Equals(config.ProviderName, "OpenRouter", StringComparison.OrdinalIgnoreCase))
                return new OpenRouterProvider(config, runtime, previewClient, previewKey);
            throw new NotSupportedException("Unknown AI provider: " + config.ProviderName);
        }
    }
}
