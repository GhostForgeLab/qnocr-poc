using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Maps one explicit local test-product JSON to a reviewable draft; never approves or publishes it.</summary>
public sealed class KnowledgeAuthoringService
{
    private readonly KnowledgeReviewService _review;
    private readonly string _draftsDirectory;
    private readonly string _auditPath;
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public KnowledgeAuthoringService(string directory, KnowledgeReviewService review)
    {
        _review = review ?? throw new ArgumentNullException(nameof(review));
        var root = Path.GetFullPath(directory);
        _draftsDirectory = Path.Combine(root, "drafts");
        _auditPath = Path.Combine(root, "authoring-audit.jsonl");
        Directory.CreateDirectory(_draftsDirectory);
    }

    public KnowledgeDraft CreateFromFile(string path, string actor)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length is < 2 or > 16 * 1024)
            throw new InvalidDataException("One bounded local test-product JSON is required.");
        var input = JsonSerializer.Deserialize<ProductKnowledgeAuthoringInput>(File.ReadAllText(file.FullName), InputOptions)
            ?? throw new InvalidDataException("Authoring input is null.");
        return Create(input, actor);
    }

    public KnowledgeDraft Create(ProductKnowledgeAuthoringInput input, string actor)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128 ||
            string.IsNullOrWhiteSpace(input.SellerId) || input.SellerId.Length > 64 ||
            !input.SellerId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
            string.IsNullOrWhiteSpace(input.ProductId) || input.ProductId.Length > 32 ||
            !input.ProductId.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(input.Version) || input.Version.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Name) ||
            input.Source is null || !Enum.IsDefined(input.Source.SourceType) ||
            string.IsNullOrWhiteSpace(input.Source.SourceName) ||
            string.IsNullOrWhiteSpace(input.Source.Reference) || input.Source.CapturedAt == default ||
            !input.IsTestData)
            throw new InvalidDataException("Authoring accepts only bounded, attributed local test data.");
        var now = DateTimeOffset.UtcNow;
        var profile = new ProductKnowledgeProfile(
            new ProductKnowledgeIdentity(input.SellerId, input.ProductId, input.Name),
            input.Capacity is null && input.Size is null && input.Weight is null ? null :
                new ProductSpecification(input.Capacity, input.Size, input.Weight),
            input.Material is null ? null : new ProductMaterial(input.Material),
            input.Features, FAQ: input.FAQ, Restrictions: input.Restrictions,
            Metadata: new ProductKnowledgeMetadata("LocalAuthoringInput", input.Version, now, true));
        var validationRecord = new ProductKnowledgeRecord(input.ProductId, new(),
            "LocalAuthoringInput", now, true, input.SellerId, input.Version, profile);
        ProductKnowledgeSchemaValidator.Validate(profile, validationRecord);
        var facts = new List<(string Category, string Key, string Value)>();
        if (input.Capacity is { Count: > 0 })
            facts.Add((KnowledgeCategories.Specification, "Capacity", string.Join(", ", input.Capacity)));
        if (input.Size is not null) facts.Add((KnowledgeCategories.Specification, "Size", input.Size));
        if (input.Weight is not null) facts.Add((KnowledgeCategories.Specification, "Weight", input.Weight));
        if (input.Material is not null)
            facts.Add((KnowledgeCategories.Material, "Material", input.Material));
        if (input.Features is not null)
            facts.AddRange(input.Features.Select(feature =>
                (KnowledgeCategories.Feature, "Feature:" + feature.FeatureName,
                    feature.Description ?? feature.FeatureName)));
        if (input.FAQ is not null)
            facts.AddRange(input.FAQ.Select(entry =>
                (KnowledgeCategories.FAQ, "FAQ:" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(entry.Question)))[..12], entry.Answer)));
        if (input.Restrictions is not null)
            facts.AddRange(input.Restrictions.Select((restriction, index) =>
                (KnowledgeCategories.Restriction, "Restriction:" + (index + 1), restriction.RestrictionText)));
        if (facts.Count == 0 || facts.Count > 18 || facts.Any(fact => fact.Value.Length > 200 || fact.Key.Length > 128) ||
            facts.Select(fact => fact.Key).Distinct(StringComparer.Ordinal).Count() != facts.Count)
            throw new InvalidDataException("Draft needs 1–18 distinct bounded fields.");
        var published = _review.Snapshot().Where(item => item.SellerId == input.SellerId &&
            item.ProductId == input.ProductId && item.Status == KnowledgeStatus.Published).ToArray();
        var conflicts = facts.Where(fact => published.Any(item => item.Key == fact.Key && item.Value != fact.Value))
            .Select(fact => fact.Key).Distinct(StringComparer.Ordinal).ToArray();
        var skipped = facts.Where(fact => published.Any(item => item.Key == fact.Key && item.Value == fact.Value))
            .Select(fact => fact.Key).Distinct(StringComparer.Ordinal).ToArray();
        if (conflicts.Length == 0 && skipped.Length == facts.Count)
            throw new InvalidOperationException("All candidate facts already exist as published knowledge.");
        // A conflict draft is retained for human correction; none of its candidate fields enters review.
        var items = conflicts.Length > 0 ? [] : _review.CreateDraftBatch(input.SellerId,
            input.ProductId, facts.Where(fact => !skipped.Contains(fact.Key, StringComparer.Ordinal)).ToArray(),
            input.Source, input.Version, actor);
        var draft = new KnowledgeDraft(Guid.NewGuid().ToString("N"), input.SellerId, input.ProductId,
            input.Source, now, conflicts.Length > 0 ? KnowledgeDraftStatus.Conflict : KnowledgeDraftStatus.Draft,
            profile, items.Select(item => item.KnowledgeId).ToArray(), conflicts, skipped);
        var path = DraftPath(draft.DraftId);
        File.WriteAllText(path, JsonSerializer.Serialize(draft));
        File.AppendAllText(_auditPath, JsonSerializer.Serialize(new
        {
            Event = conflicts.Length > 0 ? "KNOWLEDGE_DRAFT_CONFLICT" : "KNOWLEDGE_DRAFT_CREATED",
            draft.DraftId, draft.SellerId, draft.ProductId, draft.Status,
            draft.KnowledgeItemIds, draft.ConflictingFields, Actor = actor, At = now
        }) + Environment.NewLine);
        return draft;
    }

    public IReadOnlyList<KnowledgeDraft> ListDrafts() =>
        Directory.EnumerateFiles(_draftsDirectory, "*.json")
            .Select(path => GetDraft(Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(draft => draft.CreatedAt).ToArray();

    public KnowledgeDraft GetDraft(string draftId)
    {
        if (draftId.Length != 32 || !draftId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid draft ID.", nameof(draftId));
        var draft = JsonSerializer.Deserialize<KnowledgeDraft>(File.ReadAllText(DraftPath(draftId)))
            ?? throw new InvalidDataException("Draft is null.");
        if (draft.Status == KnowledgeDraftStatus.Conflict || draft.KnowledgeItemIds.Count == 0)
            return draft;
        var all = _review.Snapshot();
        var items = draft.KnowledgeItemIds.Select(id => all.SingleOrDefault(item => item.KnowledgeId == id))
            .ToArray();
        if (items.Any(item => item is null)) throw new InvalidDataException("Draft review item is missing.");
        var states = items.Select(item => item!.Status).ToArray();
        var status = states.All(state => state == KnowledgeStatus.Published) ? KnowledgeDraftStatus.Published :
            states.Any(state => state == KnowledgeStatus.Rejected) ? KnowledgeDraftStatus.Rejected :
            states.All(state => state == KnowledgeStatus.Approved) ? KnowledgeDraftStatus.Approved :
            states.All(state => state == KnowledgeStatus.Reviewing) ? KnowledgeDraftStatus.Reviewing :
            KnowledgeDraftStatus.Draft;
        return draft with { Status = status, AiUseAllowed = items.All(item => item!.AiUseAllowed) };
    }

    private string DraftPath(string id) => Path.Combine(_draftsDirectory, id + ".json");
}
