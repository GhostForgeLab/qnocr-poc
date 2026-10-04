using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Product;
using QnocrPoc.Shadow;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Projects an Agent session into the existing product resolver contract; no new session lifecycle.</summary>
public sealed class AgentProductContextAdapter
{
    private const string ProjectionBinding = "AgentObservedProductResolverProjection";
    private readonly string _root;
    private readonly SessionProductContextResolver _resolver;
    private readonly SessionRegistry _productBindings;

    public AgentProductContextAdapter(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.GetFullPath(stateRoot);
        _resolver = new SessionProductContextResolver(new ManualProductResolver(_root), new OcrProductResolver());
        // Only reuse BindCurrentProduct; never call Resolve to create a Shadow buyer session.
        _productBindings = new SessionRegistry(_root, "AgentProductProjection", null);
    }

    public static string ResolverSessionId(string agentSessionId)
    {
        if (!Guid.TryParseExact(agentSessionId, "N", out _))
            throw new ArgumentException("An existing Agent SessionId is required.", nameof(agentSessionId));
        // Namespaced projection ID satisfies the existing Manual Resolver's 64-hex file contract.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("AgentProductResolver/v1:" + agentSessionId)));
    }

    public MessageSession Prepare(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status != AgentSessionStatus.Active || string.IsNullOrWhiteSpace(session.Identity.SellerId) ||
            string.IsNullOrWhiteSpace(session.Identity.BuyerId))
            throw new InvalidDataException("Product resolution requires an Active, seller-scoped Agent session.");
        var id = ResolverSessionId(session.SessionId);
        var folder = Path.Combine(_root, "sessions", id);
        var sessionPath = Path.Combine(folder, "session.json");
        if (File.Exists(sessionPath))
        {
            var prior = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(sessionPath));
            if (prior is null || prior.SessionId != id || prior.SellerId != session.Identity.SellerId ||
                prior.BuyerId != session.Identity.BuyerId || prior.IdentityBinding != ProjectionBinding)
                throw new InvalidDataException("Product resolver projection differs from its Agent session.");
        }
        var projection = new MessageSession(id, session.Identity.SellerId, session.Identity.BuyerId,
            session.Identity.BuyerId, ProjectionBinding, "", null,
            CreatedAt: session.CreatedTime, LastActivityAt: session.LastActiveTime,
            ProductContextBindingStatus: ProductContextBindingStatus.Unknown);
        // Never copy a previous CurrentProductContext or ProductReference from this sidecar.
        AtomicJson.Write(sessionPath, projection);
        AtomicJson.Write(Path.Combine(folder, "agent-session-link.json"), new
        {
            AgentSessionId = session.SessionId, ResolverSessionId = id,
            session.Identity.Source, session.Identity.SellerId, session.Identity.BuyerId,
            IdentityLevel = session.Identity.IdentityLevel
        });
        return projection;
    }

    public ProductContextResolution Resolve(AgentSession session, OcrResult? currentProductOcr,
        DateTimeOffset capturedAt, string captureId)
    {
        var projection = Prepare(session);
        var resolution = _resolver.Resolve(projection, currentProductOcr, capturedAt, captureId);
        // Reuse existing binding validation, references and audit; no global/product-history lookup.
        _productBindings.BindCurrentProduct(projection, resolution, capturedAt, captureId);
        return resolution with
        {
            Current = resolution.Current is null ? null : resolution.Current with { SessionId = session.SessionId }
        };
    }
}
