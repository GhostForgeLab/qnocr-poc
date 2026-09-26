using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>No I/O, product facts, FAQ, or retrieval. Proves the provider seam while reporting all fields missing.</summary>
public sealed class EmptyKnowledgeProvider : IKnowledgeProvider
{
    public Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new KnowledgeResult(false, "None", null, "",
            request.NeedFields.Distinct(StringComparer.Ordinal).ToArray()));
    }
}
