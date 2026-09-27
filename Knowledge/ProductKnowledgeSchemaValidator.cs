using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Validates a bounded test profile; no inference, enrichment, or external lookup.</summary>
public static class ProductKnowledgeSchemaValidator
{
    public static void Validate(ProductKnowledgeProfile profile, ProductKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(record);
        if (profile.Identity is null || string.IsNullOrWhiteSpace(profile.Identity.SellerId) ||
            string.IsNullOrWhiteSpace(profile.Identity.ProductId) ||
            profile.Identity.SellerId != record.SellerId || profile.Identity.ProductId != record.ProductId)
            throw new InvalidDataException("Profile SellerId/ProductId must match the record identity.");
        if (!ValidOptional(profile.Identity.ProductName, 100))
            throw new InvalidDataException("Invalid optional product name.");
        if (profile.Specification?.Capacity is { Count: > 0 } capacities &&
            record.Fields.TryGetValue("Capacity", out var legacyCapacity) &&
            legacyCapacity != string.Join(", ", capacities))
            throw new InvalidDataException("Legacy Capacity conflicts with typed specification.");
        if (profile.Material is { } typedMaterial && record.Fields.TryGetValue("Material", out var legacyMaterial) &&
            legacyMaterial != typedMaterial.MaterialName)
            throw new InvalidDataException("Legacy Material conflicts with typed material.");
        if (profile.Specification is { } specification)
        {
            if (specification.Capacity is { Count: > 16 } ||
                specification.Capacity is { Count: > 0 } values && string.Join(", ", values).Length > 200 ||
                specification.Capacity?.Any(value => !Valid(value, 100)) == true ||
                !ValidOptional(specification.Size, 100) || !ValidOptional(specification.Weight, 100))
                throw new InvalidDataException("Invalid specification field.");
        }
        if (profile.Material is { } material &&
            (!Valid(material.MaterialName, 100) || !ValidOptional(material.Description, 500)))
            throw new InvalidDataException("Invalid material field.");
        if (profile.Features is { } features &&
            (features.Count > 16 || features.Any(feature => feature is null ||
                !Valid(feature.FeatureName, 100) || !ValidOptional(feature.Description, 500))))
            throw new InvalidDataException("Invalid feature field.");
        if (profile.Scenarios is { } scenarios &&
            (scenarios.Count > 16 || scenarios.Any(scenario => scenario is null ||
                !Valid(scenario.ScenarioName, 100) || !ValidOptional(scenario.Description, 500))))
            throw new InvalidDataException("Invalid scenario field.");
        if (profile.FAQ is { } faq &&
            (faq.Count > 8 || faq.Any(entry => entry is null ||
                !Valid(entry.Question, 300) || !Valid(entry.Answer, 500))))
            throw new InvalidDataException("Invalid FAQ field.");
        if (profile.AfterSales is { } afterSales &&
            (!ValidOptional(afterSales.ReturnPolicy, 500) || !ValidOptional(afterSales.Warranty, 500)))
            throw new InvalidDataException("Invalid after-sales field.");
        if (profile.Restrictions is { } restrictions &&
            (restrictions.Count > 16 || restrictions.Any(item => item is null || !Valid(item.RestrictionText, 500))))
            throw new InvalidDataException("Invalid restriction field.");
        if (profile.Metadata is { } metadata &&
            (metadata.Source != record.Source || metadata.Version != record.Version ||
                metadata.UpdatedAt != record.UpdatedAt || metadata.IsTestData != record.IsTestData))
            throw new InvalidDataException("Profile metadata must match the record source/version/time/test marker.");
    }

    private static bool ValidOptional(string? value, int maximum) => value is null || Valid(value, maximum);
    private static bool Valid(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}');
}
