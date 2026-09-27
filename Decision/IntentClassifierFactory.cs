using System.Net.Http;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Decision;

public sealed class IntentProviderSelection(IIntentClassifier classifier, string selectedProvider,
    bool fallbackUsed) : IDisposable
{
    public IIntentClassifier Classifier { get; } = classifier;
    public string SelectedProvider { get; } = selectedProvider;
    public bool FallbackUsed { get; } = fallbackUsed;
    public void Dispose() => (Classifier as IDisposable)?.Dispose();
}

/// <summary>Selects the runtime classifier once; JEV failures fall back for that message only.</summary>
public static class IntentClassifierFactory
{
    public static IntentProviderSelection Create(string configPath,
        Action<IntentProviderTrace>? trace = null, HttpClient? jevClient = null,
        Func<string?>? apiKey = null)
    {
        var config = IntentProviderConfig.Load(configPath);
        if (!config.Enabled || config.Provider == "RuleBased")
            return RuleOnly(trace, fallbackUsed: false);
        var key = apiKey ?? (() => Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"));
        if (string.IsNullOrWhiteSpace(key()))
        {
            Emit(trace, "JEV_PROVIDER_UNAVAILABLE", "JEV", null, true, null, "ApiKeyMissing");
            return RuleOnly(trace, fallbackUsed: true);
        }
        JEVProviderConfig jevConfig;
        try
        {
            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            jevConfig = JEVProviderConfig.Load(Path.GetFullPath(Path.Combine(configDir, config.JevConfigPath)));
            if (!jevConfig.Enabled) throw new InvalidDataException("JEV endpoint is disabled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            Emit(trace, "JEV_PROVIDER_UNAVAILABLE", "JEV", null, true, null, ex.GetType().Name);
            return RuleOnly(trace, fallbackUsed: true);
        }
        var jev = new JEVIntentClassifier(jevConfig, jevClient, key);
        Emit(trace, "INTENT_PROVIDER_SELECTED", "JEV", null, false, null, null);
        return new IntentProviderSelection(new RuntimeClassifier(jev, trace), "JEV", false);
    }

    public static IntentProviderSelection RuleOnly(Action<IntentProviderTrace>? trace = null,
        bool fallbackUsed = false)
    {
        Emit(trace, "INTENT_PROVIDER_SELECTED", "RuleBased", null, fallbackUsed, null, null);
        return new IntentProviderSelection(new RuntimeClassifier(null, trace, fallbackUsed),
            "RuleBased", fallbackUsed);
    }

    private static void Emit(Action<IntentProviderTrace>? trace, string eventName, string provider,
        string? intent, bool fallbackUsed, SessionContextView? context, string? reason)
    {
        trace?.Invoke(new IntentProviderTrace(eventName, provider, intent, fallbackUsed,
            DateTimeOffset.UtcNow, context?.SessionId, context?.MessageId, context?.CaptureId, reason));
    }

    private sealed class RuntimeClassifier(JEVIntentClassifier? jev,
        Action<IntentProviderTrace>? trace, bool startupFallback = false) : IIntentClassifier, IDisposable
    {
        private readonly RuleBasedIntentClassifier _rule = new();
        public IntentResult Classify(SessionContextView context)
        {
            if (jev is not null)
            {
                try
                {
                    var result = jev.Classify(context);
                    Emit(trace, "INTENT_PROVIDER_USED", "JEV", result.IntentName, false, context, null);
                    return result;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Emit(trace, "JEV_PROVIDER_FAILED", "JEV", null, true, context, ex.GetType().Name);
                    var fallback = _rule.Classify(context);
                    Emit(trace, "INTENT_PROVIDER_USED", "RuleBased", fallback.IntentName, true, context, null);
                    return fallback;
                }
            }
            var rule = _rule.Classify(context);
            Emit(trace, "INTENT_PROVIDER_USED", "RuleBased", rule.IntentName, startupFallback, context, null);
            return rule;
        }
        public void Dispose() => jev?.Dispose();
    }
}
