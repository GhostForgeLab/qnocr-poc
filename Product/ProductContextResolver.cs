using System.Text.Json;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Product;

public sealed class ProductContextResolver
{
    private readonly Dictionary<string, ProductProfile> _profiles = new(StringComparer.Ordinal);
    private readonly ProductIdExtractor _extractor;
    public string CatalogPath { get; }
    public bool IsTestCatalog { get; }

    public ProductContextResolver(string catalogPath, ProductIdExtractor? extractor = null)
    {
        CatalogPath = Path.GetFullPath(catalogPath);
        _extractor = extractor ?? new ProductIdExtractor();
        var catalog = JsonSerializer.Deserialize<ProductProfileCatalog>(File.ReadAllText(CatalogPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Product profile catalog is null.");
        if (catalog.SchemaVersion != 1 || catalog.Profiles is null)
            throw new InvalidDataException("Product profile catalog requires SchemaVersion=1 and Profiles array.");
        IsTestCatalog = catalog.IsTestCatalog;
        foreach (var profile in catalog.Profiles)
        {
            if (profile is null || !ProductIdExtractor.IsValidProductId(profile.ProductId) ||
                string.IsNullOrWhiteSpace(profile.ProfileKey) || string.IsNullOrWhiteSpace(profile.DisplayName) ||
                IsTestCatalog && !profile.IsTestData)
                throw new InvalidDataException("Invalid profile: string ID, ProfileKey, DisplayName and consistent test-data flags are required.");
            if (!_profiles.TryAdd(profile.ProductId, profile)) throw new InvalidDataException($"Duplicate product ID in catalog: {profile.ProductId}");
        }
    }

    public ProductContext Resolve(OcrResult ocr)
    {
        var extraction = _extractor.Extract(ocr);
        ProductProfile? profile = null;
        var status = extraction.Status switch
        {
            ProductIdStatus.NotFound => ProductContextStatus.NotFound,
            ProductIdStatus.LowConfidence => ProductContextStatus.LowConfidence,
            ProductIdStatus.Ambiguous => ProductContextStatus.Ambiguous,
            _ => _profiles.TryGetValue(extraction.ProductId!, out profile) ? ProductContextStatus.Matched : ProductContextStatus.Unmapped
        };
        return new(status, extraction.ProductId, extraction.Confidence, profile, IsTestCatalog, CatalogPath, extraction);
    }
}
