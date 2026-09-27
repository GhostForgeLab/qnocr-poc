using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Storage-neutral repository; source adapters own parsing, files, and source-specific validation.</summary>
public class ProductKnowledgeRepository : IProductKnowledgeRepository
{
    private readonly IKnowledgeSource _source;
    public ProductKnowledgeRepository(IKnowledgeSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public KnowledgeStorageSourceType SourceType => _source.SourceType;

    public async Task<ProductKnowledgeRecord?> GetAsync(string sellerId, string productId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = await _source.ReadAsync(sellerId, productId, cancellationToken);
        if (record is null) return null;
        if (record.SellerId != sellerId || record.ProductId != productId)
            throw new InvalidDataException("Knowledge source returned a record outside the requested scope.");
        return record with { StorageSourceType = _source.SourceType };
    }

    /// <summary>Explicit audit lookup; the AI provider uses only GetAsync.</summary>
    public Task<KnowledgeItem?> GetPublishedVersionAsync(string sellerId, string productId,
        string key, string version, CancellationToken cancellationToken = default) =>
        _source.ReadPublishedVersionAsync(sellerId, productId, key, version, cancellationToken);
}
