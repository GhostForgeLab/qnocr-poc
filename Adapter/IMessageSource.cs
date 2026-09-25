using QnocrPoc.Models;

namespace QnocrPoc.Adapter;

/// <summary>A pull-based source of unified events. No delivery, deduplication, or transport guarantee is implied.</summary>
public interface IMessageSource
{
    string Source { get; }
    IAsyncEnumerable<MessageEnvelope> ReadAsync(CancellationToken cancellationToken = default);
}
