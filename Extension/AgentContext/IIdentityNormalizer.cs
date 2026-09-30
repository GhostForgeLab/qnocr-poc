namespace QnocrPoc.Extension.AgentContext;

/// <summary>Normalizes an Observed buyer label; never establishes a verified platform identity.</summary>
public interface IIdentityNormalizer
{
    string Normalize(string observedBuyerLabel);
}
