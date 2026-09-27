namespace QnocrPoc.Models;

/// <summary>Typed, source-neutral shape. Identity is required; all knowledge sections are optional.</summary>
public sealed record ProductKnowledgeProfile(
    ProductKnowledgeIdentity Identity,
    ProductSpecification? Specification = null,
    ProductMaterial? Material = null,
    IReadOnlyList<ProductFeature>? Features = null,
    IReadOnlyList<ProductScenario>? Scenarios = null,
    IReadOnlyList<ProductFAQ>? FAQ = null,
    ProductAfterSales? AfterSales = null,
    IReadOnlyList<ProductRestriction>? Restrictions = null,
    ProductKnowledgeMetadata? Metadata = null);

public sealed record ProductKnowledgeIdentity(string SellerId, string ProductId);
public sealed record ProductSpecification(IReadOnlyList<string>? Capacity = null, string? Size = null, string? Weight = null);
public sealed record ProductMaterial(string MaterialName, string? Description = null);
public sealed record ProductFeature(string FeatureName, string? Description = null);
public sealed record ProductScenario(string ScenarioName, string? Description = null);
public sealed record ProductFAQ(string Question, string Answer);
public sealed record ProductAfterSales(string? ReturnPolicy = null, string? Warranty = null);
public sealed record ProductRestriction(string RestrictionText);
public sealed record ProductKnowledgeMetadata(string Source, string Version, DateTimeOffset UpdatedAt, bool IsTestData);

public static class KnowledgeCategories
{
    public const string Specification = "Specification";
    public const string Material = "Material";
    public const string Feature = "Feature";
    public const string Scenario = "Scenario";
    public const string FAQ = "FAQ";
    public const string AfterSales = "AfterSales";
    public const string Restriction = "Restriction";
    public const string Mixed = "Mixed";
    public const string Unknown = "Unknown";
}
