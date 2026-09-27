using System.Text.Json;
using System.Text.Json.Serialization;

namespace QnocrPoc.Decision;

/// <summary>JEV is opt-in and separate from the existing reply-generation provider.</summary>
public sealed record JEVProviderConfig(string Endpoint, string Model, int TimeoutSeconds, bool Enabled)
{
    public static JEVProviderConfig Load(string path)
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var config = JsonSerializer.Deserialize<JEVProviderConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("JEV config is null.");
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(config.Model) || config.Model.Length > 80 ||
            config.TimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("JEV config needs HTTPS endpoint, model and 1–120 second timeout.");
        return config;
    }
}
