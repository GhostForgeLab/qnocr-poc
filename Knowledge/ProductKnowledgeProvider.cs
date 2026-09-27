using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Read-only field resolver; the repository owns all storage and store-scoping decisions.</summary>
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
            if (record.Fields.TryGetValue(field, out var value)) returned[field] = value;
            else missing.Add(field);
        var complete = missing.Count == 0 && returned.Count > 0;
        var content = complete ? string.Join("\n", returned.Select(pair => pair.Key + ": " + pair.Value)) : "";
        return new KnowledgeResult(complete, record.Source, null, content, missing, returned,
            record.UpdatedAt, false, record.Version, record.SellerId);
    }
}
