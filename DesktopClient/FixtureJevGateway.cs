using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Models;

namespace QNAgent;

// Explicit test dependency only; never selected by settings or the production constructors.
internal sealed class FixtureJevGateway : IJevGateway
{
    public Task<JevAssessment> AnalyzeAsync(ClientSettings settings, string evidence, CancellationToken token)
    {
        using var document = JsonDocument.Parse(evidence);
        var mode = document.RootElement.GetProperty("Product").ValueKind == JsonValueKind.Null ? "HUMAN_CHECK" : "ANSWER";
        return Task.FromResult(new JevAssessment(new IntentResult("MATERIAL_QUERY", 1, true, true, true, "Medium", "JEV/fixture"),
            mode, "fixture", new JevChoice(mode, 1, 1)));
    }
    public Task<JevVerification> VerifyAsync(ClientSettings settings, string evidence, string draft, string mode, CancellationToken token) =>
        Task.FromResult(new JevVerification(true, "fixture", new JevChoice("SUPPORTED", 1, 1)));
}
