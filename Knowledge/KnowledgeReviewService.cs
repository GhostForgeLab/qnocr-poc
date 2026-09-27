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

    /// <summary>Create all fields of one explicit product draft with one state write.</summary>
    public IReadOnlyList<KnowledgeItem> CreateDraftBatch(string sellerId, string productId,
        IReadOnlyList<(string Category, string Key, string Value)> facts,
        KnowledgeSource source, string version, string actor)
    {
        Require(sellerId, nameof(sellerId)); Require(productId, nameof(productId));
        Require(version, nameof(version)); Require(actor, nameof(actor));
        ArgumentNullException.ThrowIfNull(source);
        Require(source.SourceName, nameof(source.SourceName)); Require(source.Reference, nameof(source.Reference));
        if (!Enum.IsDefined(source.SourceType) || source.CapturedAt == default ||
            facts is null || facts.Count is < 1 or > 18 ||
            facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != facts.Count)
            throw new ArgumentException("Invalid bounded authoring batch.");
        foreach (var fact in facts)
        {
            Require(fact.Category, nameof(fact.Category)); Require(fact.Key, nameof(fact.Key));
            Require(fact.Value, nameof(fact.Value));
            if (fact.Value.Length > 200 || fact.Value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}'))
                throw new ArgumentException("Invalid bounded knowledge value.");
        }
        var now = DateTimeOffset.UtcNow;
        var created = facts.Select(fact => new KnowledgeItem(Guid.NewGuid().ToString("N"),
            sellerId, productId, fact.Category, fact.Key, fact.Value, source, now, now,
            KnowledgeStatus.Draft, new KnowledgeVersion(version, now, null, KnowledgeStatus.Draft))).ToArray();
        lock (_gate)
        {
            var items = Read();
            if (facts.Any(fact => items.Any(item => item.SellerId == sellerId &&
                item.ProductId == productId && item.Key == fact.Key &&
                item.Status == KnowledgeStatus.Published && item.Value != fact.Value)))
                throw new InvalidOperationException("A published value changed during draft creation.");
            if (items.Count + created.Length > 256) throw new InvalidDataException("Knowledge test state exceeds 256 items.");
            items.AddRange(created);
            foreach (var item in created) Audit("KNOWLEDGE_CREATED", item, actor);
            Save(items);
        }
        return created;
    }
    public KnowledgeItem EditDraft(string knowledgeId, string value, string actor)
    {
        Require(knowledgeId, nameof(knowledgeId)); Require(value, nameof(value)); Require(actor, nameof(actor));
        if (value.Length > 200 || value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}'))
            throw new ArgumentException("Invalid bounded knowledge value.", nameof(value));
        lock (_gate)
        {
            var items = Read();
            var index = items.FindIndex(item => item.KnowledgeId == knowledgeId);
            if (index < 0) throw new KeyNotFoundException("Knowledge item not found.");
            var before = items[index];
            if (before.Status != KnowledgeStatus.Draft)
                throw new InvalidOperationException("Only Draft knowledge may be edited.");
            if (items.Any(item => item.KnowledgeId != knowledgeId && item.SellerId == before.SellerId &&
                item.ProductId == before.ProductId && item.Key == before.Key &&
                item.Status == KnowledgeStatus.Published && item.AiUseAllowed && item.Value != value))
                throw new InvalidOperationException("A published value differs; create a revision instead.");
            var updated = before with { Value = value, UpdatedAt = DateTimeOffset.UtcNow };
            items[index] = updated;
            Audit("KNOWLEDGE_DRAFT_EDITED", updated, actor);
            Save(items);
            return updated;
        }
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

    /// <summary>Human-triggered atomic switch: retain v1 as published history, activate approved v2.</summary>
    public KnowledgeItem PublishReplacement(string candidateId, string baseId, string baseVersion, string actor)
    {
        Require(candidateId, nameof(candidateId)); Require(baseId, nameof(baseId));
        Require(baseVersion, nameof(baseVersion)); Require(actor, nameof(actor));
        lock (_gate)
        {
            var items = Read();
            var oldIndex = items.FindIndex(item => item.KnowledgeId == baseId);
            var newIndex = items.FindIndex(item => item.KnowledgeId == candidateId);
            if (oldIndex < 0 || newIndex < 0) throw new KeyNotFoundException("Revision item is missing.");
            var old = items[oldIndex]; var candidate = items[newIndex];
            if (old.Status != KnowledgeStatus.Published || !old.AiUseAllowed ||
                old.Version.Version != baseVersion || candidate.Status != KnowledgeStatus.Approved ||
                candidate.SellerId != old.SellerId || candidate.ProductId != old.ProductId ||
                candidate.Key != old.Key || candidate.Version.Version == old.Version.Version ||
                string.IsNullOrWhiteSpace(candidate.Version.ApprovedBy) ||
                items.Any(item => item.KnowledgeId != old.KnowledgeId && item.KnowledgeId != candidate.KnowledgeId &&
                    item.SellerId == old.SellerId && item.ProductId == old.ProductId && item.Key == old.Key &&
                    item.Status == KnowledgeStatus.Published && item.AiUseAllowed))
                throw new InvalidOperationException("The approved revision no longer matches the active base version.");
            var now = DateTimeOffset.UtcNow;
            var retained = old with { AiUseAllowed = false, UpdatedAt = now,
                SupersededByKnowledgeId = candidate.KnowledgeId };
            var active = candidate with { Status = KnowledgeStatus.Published, AiUseAllowed = true,
                UpdatedAt = now, Version = candidate.Version with { Status = KnowledgeStatus.Published } };
            items[oldIndex] = retained; items[newIndex] = active;
            Audit("KNOWLEDGE_SUPERSEDED", retained, actor);
            Audit("KNOWLEDGE_PUBLISHED", active, actor);
            Save(items);
            return active;
        }
    }
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
