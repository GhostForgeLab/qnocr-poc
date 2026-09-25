namespace QnocrPoc.Shadow;

/// <summary>Reads the durable context beside the original one-message envelope.</summary>
public sealed class AgentShadowClient
{
    private readonly ShadowAdapterClient _client = new(aiShadow: true);
    public Task<ShadowAdapterResult> ConvertAsync(string envelopePath, string? identityPath, string outputDirectory, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(envelopePath))!, "context.json")))
            return Task.FromResult(new ShadowAdapterResult("ContextUnavailable", 0, null, "Durable session context is missing."));
        return _client.ConvertAsync(envelopePath, identityPath, outputDirectory, token);
    }
}
