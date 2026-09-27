using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Explicit, local human review workflow for bounded test knowledge. No automatic transitions.</summary>
public sealed class KnowledgeReviewService
{
    private readonly string _itemsPath;
    private readonly string _auditPath;
    private readonly object _gate = new();

    public KnowledgeReviewService(string directory)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        _itemsPath = Path.Combine(root, "knowledge-items.json");
        _auditPath = Path.Combine(root, "knowledge-audit.jsonl");
    }

    public string ItemsPath => _itemsPath;
    public string AuditPath => _auditPath;

    public KnowledgeItem CreateDraft(string sellerId, string productId, string category, string key,
        string value, KnowledgeSource source, string version, string actor)
    {
        Require(actor, nameof(actor));
        Require(sellerId, nameof(sellerId)); Require(productId, nameof(productId));
        Require(category, nameof(category)); Require(key, nameof(key));
        Require(value, nameof(value)); Require(version, nameof(version));
        ArgumentNullException.ThrowIfNull(source);
        Require(source.SourceName, nameof(source.SourceName));
        Require(source.Reference, nameof(source.Reference));
        if (!Enum.IsDefined(source.SourceType) || source.CapturedAt == default || value.Length > 200 ||
            value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}'))
            throw new ArgumentException("Invalid bounded test fact or source.");
        var now = DateTimeOffset.UtcNow;
        var item = new KnowledgeItem(Guid.NewGuid().ToString("N"), sellerId, productId, category,
            key, value, source, now, now, KnowledgeStatus.Draft,
            new KnowledgeVersion(version, now, null, KnowledgeStatus.Draft));
        lock (_gate)
        {
            var items = Read();
            items.Add(item);
            Audit("KNOWLEDGE_CREATED", item, actor);
            Save(items);
        }
        return item;
    }

    public KnowledgeItem SubmitForReview(string knowledgeId, string actor) =>
        Transition(knowledgeId, KnowledgeStatus.Draft, KnowledgeStatus.Reviewing, actor);

    public KnowledgeItem Approve(string knowledgeId, string actor) =>
        Transition(knowledgeId, KnowledgeStatus.Reviewing, KnowledgeStatus.Approved, actor);

    public KnowledgeItem Reject(string knowledgeId, string actor, string reason)
    {
        Require(reason, nameof(reason));
        return Transition(knowledgeId, KnowledgeStatus.Reviewing, KnowledgeStatus.Rejected, actor, reason);
    }

    public KnowledgeItem Publish(string knowledgeId, string actor, bool allowAiUse) =>
        Transition(knowledgeId, KnowledgeStatus.Approved, KnowledgeStatus.Published, actor,
            allowAiUse: allowAiUse);

    public IReadOnlyList<KnowledgeItem> Snapshot()
    {
        lock (_gate) return Read();
    }

    private KnowledgeItem Transition(string id, KnowledgeStatus expected, KnowledgeStatus next,
        string actor, string? reason = null, bool allowAiUse = false)
    {
        Require(id, nameof(id)); Require(actor, nameof(actor));
        lock (_gate)
        {
            var items = Read();
            var index = items.FindIndex(x => x.KnowledgeId == id);
            if (index < 0) throw new KeyNotFoundException("Knowledge item not found.");
            var before = items[index];
            if (before.Status != expected)
                throw new InvalidOperationException($"Expected {expected}; found {before.Status}.");
            if (next == KnowledgeStatus.Published && allowAiUse && items.Any(item =>
                item.KnowledgeId != id && item.SellerId == before.SellerId &&
                item.ProductId == before.ProductId && item.Key == before.Key &&
                item.Status == KnowledgeStatus.Published && item.AiUseAllowed))
                throw new InvalidOperationException("Another published item already owns this seller/product field.");
            var now = DateTimeOffset.UtcNow;
            var updated = before with
            {
                Status = next,
                UpdatedAt = now,
                ReviewedBy = next is KnowledgeStatus.Approved or KnowledgeStatus.Rejected ? actor : before.ReviewedBy,
                ReviewedAt = next is KnowledgeStatus.Approved or KnowledgeStatus.Rejected ? now : before.ReviewedAt,
                RejectionReason = reason,
                AiUseAllowed = next == KnowledgeStatus.Published && allowAiUse,
                Version = before.Version with
                {
                    Status = next,
                    ApprovedBy = next is KnowledgeStatus.Approved or KnowledgeStatus.Published
                        ? before.Version.ApprovedBy ?? actor : null
                }
            };
            items[index] = updated;
            Audit(next switch
            {
                KnowledgeStatus.Approved => "KNOWLEDGE_REVIEWED",
                KnowledgeStatus.Rejected => "KNOWLEDGE_REJECTED",
                KnowledgeStatus.Published => "KNOWLEDGE_PUBLISHED",
                _ => "KNOWLEDGE_REVIEW_SUBMITTED"
            }, updated, actor);
            Save(items);
            return updated;
        }
    }

    private List<KnowledgeItem> Read() => File.Exists(_itemsPath)
        ? JsonSerializer.Deserialize<List<KnowledgeItem>>(File.ReadAllText(_itemsPath))
            ?? throw new InvalidDataException("Knowledge state is null.")
        : [];

    private void Save(List<KnowledgeItem> items)
    {
        if (items.Count > 256) throw new InvalidDataException("Knowledge test state exceeds 256 items.");
        var temporary = _itemsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items));
        File.Move(temporary, _itemsPath, true);
    }

    private void Audit(string eventName, KnowledgeItem item, string actor) =>
        File.AppendAllText(_auditPath, JsonSerializer.Serialize(new
        {
            Event = eventName, item.KnowledgeId, item.SellerId, item.ProductId,
            item.Category, item.Key, item.Status, item.Version.Version,
            item.AiUseAllowed, Actor = actor, At = item.UpdatedAt
        }) + Environment.NewLine);

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("A bounded nonempty value is required.", name);
    }
}
