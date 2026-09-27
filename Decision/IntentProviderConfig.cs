using System.Text.Json;
using System.Text.Json.Serialization;

namespace QnocrPoc.Decision;

/// <summary>Top-level runtime selection; the V1.26 comparison config stays separately default-disabled.</summary>
public sealed record IntentProviderConfig(string Provider, string Fallback, bool Enabled,
    string JevConfigPath)
{
    public static IntentProviderConfig Load(string path)
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var config = JsonSerializer.Deserialize<IntentProviderConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("Intent provider config is null.");
        if (config.Provider is not ("JEV" or "RuleBased") || config.Fallback != "RuleBased" ||
            string.IsNullOrWhiteSpace(config.JevConfigPath) || config.JevConfigPath.Length > 256)
            throw new InvalidDataException("Intent provider must select JEV or RuleBased with RuleBased fallback.");
        return config;
    }
}
