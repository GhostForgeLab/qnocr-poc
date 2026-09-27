using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Read-only seller/product storage boundary. Future adapters must preserve the same scope.</summary>
public interface IKnowledgeSource
{
    KnowledgeStorageSourceType SourceType { get; }
    Task<ProductKnowledgeRecord?> ReadAsync(string sellerId, string productId,
        CancellationToken cancellationToken = default);
    Task<KnowledgeItem?> ReadPublishedVersionAsync(string sellerId, string productId,
        string key, string version, CancellationToken cancellationToken = default);
}
