using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Read-only field resolver; the repository owns storage and seller scoping.</summary>
public sealed class ProductKnowledgeProvider(IProductKnowledgeRepository repository) : IKnowledgeProvider
{
    private readonly IProductKnowledgeRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var needed = request.NeedFields.Distinct(StringComparer.Ordinal).ToArray();
        if (needed.Length == 0)
            return new KnowledgeResult(false, "None", null, "", needed);
        var productId = request.ProductReference?.ProductId;
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(request.SellerId))
            return new KnowledgeResult(false, "None", null, "", needed, MissingProduct: true);
        var record = await _repository.GetAsync(request.SellerId, productId, cancellationToken);
        if (record is null)
            return new KnowledgeResult(false, "None", null, "", needed, MissingProduct: true);
        if (record.SellerId != request.SellerId || record.ProductId != productId)
            throw new InvalidDataException("Repository returned a record outside the requested seller/product scope.");
        var returned = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var field in needed)
        {
            var value = FromProfile(record.Profile, field);
            if (value is null) record.Fields.TryGetValue(field, out value);
            if (string.IsNullOrWhiteSpace(value)) missing.Add(field);
            else returned[field] = value;
        }
        var complete = missing.Count == 0 && returned.Count > 0;
        var content = complete ? string.Join("\n", returned.Select(pair => pair.Key + ": " + pair.Value)) : "";
        var categories = returned.Keys.Select(CategoryOf).Distinct(StringComparer.Ordinal).ToArray();
        var category = categories.Length switch { 0 => null, 1 => categories[0], _ => KnowledgeCategories.Mixed };
        return new KnowledgeResult(complete, record.Source, null, content, missing, returned,
            record.UpdatedAt, false, record.Version, record.SellerId, category);
    }

    private static string? FromProfile(ProductKnowledgeProfile? profile, string field) => field switch
    {
        "Capacity" => profile?.Specification?.Capacity is { Count: > 0 } capacities
            ? string.Join(", ", capacities) : null,
        "Size" => profile?.Specification?.Size,
        "Weight" => profile?.Specification?.Weight,
        "Material" => profile?.Material?.MaterialName,
        _ => null
    };
    private static string CategoryOf(string field) => field switch
    {
        "Capacity" or "Size" or "Weight" => KnowledgeCategories.Specification,
        "Material" => KnowledgeCategories.Material,
        "CustomizationPolicy" => KnowledgeCategories.Feature,
        _ => KnowledgeCategories.Unknown
    };
}
