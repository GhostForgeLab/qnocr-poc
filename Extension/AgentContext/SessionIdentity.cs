namespace QnocrPoc.Extension.AgentContext;

public enum IdentityLevel
{
    Observed,
    SessionBound,
    VerifiedCandidate
}

/// <summary>Exact, case-sensitive source/seller/buyer observation key; not platform identity proof.</summary>
public sealed record SessionIdentity(string Source, string SellerId, string BuyerId)
{
    // Higher levels are vocabulary reserved for future evidence, never inferred by this PoC.
    public IdentityLevel IdentityLevel { get; } = IdentityLevel.Observed;
}
