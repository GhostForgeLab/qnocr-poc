using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

public interface IKnowledgeProvider
{
    Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken cancellationToken = default);
}
