using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Read-only store-and-product lookup. A missing store is never replaced with another store's record.</summary>
public interface IProductKnowledgeRepository
{
    Task<ProductKnowledgeRecord?> GetAsync(string sellerId, string productId,
        CancellationToken cancellationToken = default);
}
