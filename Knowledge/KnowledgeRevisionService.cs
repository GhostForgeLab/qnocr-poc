using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>One-field, human-reviewed test revisions; old published items remain immutable history.</summary>
public sealed class KnowledgeRevisionService
{
    private readonly KnowledgeReviewService _review;
    private readonly string _root;
    private readonly string _auditPath;

    public KnowledgeRevisionService(string directory, KnowledgeReviewService review)
    {
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _root = Path.Combine(Path.GetFullPath(directory), "revisions");
        Directory.CreateDirectory(_root);
        _auditPath = Path.Combine(Path.GetFullPath(directory), "revision-audit.jsonl");
    }

    public KnowledgeRevision Create(string sellerId, string productId, string key, string newValue,
        string reason, string candidateVersion, KnowledgeSource source, string actor)
    {
        if (string.IsNullOrWhiteSpace(sellerId) || string.IsNullOrWhiteSpace(productId) ||
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(newValue) ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 200 ||
            string.IsNullOrWhiteSpace(candidateVersion) || candidateVersion.Length > 64 ||
            source is null || !Enum.IsDefined(source.SourceType) ||
            string.IsNullOrWhiteSpace(source.SourceName) ||
            string.IsNullOrWhiteSpace(source.Reference) || source.CapturedAt == default ||
            string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An attributed, bounded single-field revision is required.");
        using var lease = AcquireLock();
        var active = _review.Snapshot().Where(item => item.SellerId == sellerId &&
            item.ProductId == productId && item.Key == key &&
            item.Status == KnowledgeStatus.Published && item.AiUseAllowed).ToArray();
        if (active.Length != 1)
            throw new InvalidOperationException("Exactly one active published base item is required.");
        var basis = active[0];
        if (_review.Snapshot().Any(item => item.SellerId == sellerId &&
            item.ProductId == productId && item.Key == key && item.Version.Version == candidateVersion))
            throw new InvalidOperationException("A field version cannot be reused.");
        if (basis.Value == newValue || basis.Version.Version == candidateVersion)
            throw new InvalidOperationException("Revision requires a changed value and a new version.");
        var changed = new KnowledgeFieldChange(key, basis.Value, newValue, reason);
        var pending = Directory.EnumerateFiles(_root, "*.json")
            .Select(Read)
            .Any(revision => revision.SellerId == sellerId && revision.ProductId == productId &&
                revision.BaseKnowledgeId == basis.KnowledgeId &&
                revision.ChangedFields.Any(field => field.Key == key) &&
                GetStatus(revision) is KnowledgeRevisionStatus.Draft or KnowledgeRevisionStatus.Reviewing or KnowledgeRevisionStatus.Approved);
        var candidate = pending ? null : _review.CreateDraft(sellerId, productId, basis.Category,
            key, newValue, source, candidateVersion, actor);
        var revision = new KnowledgeRevision(Guid.NewGuid().ToString("N"), sellerId, productId,
            basis.Version.Version, [changed], DateTimeOffset.UtcNow,
            pending ? KnowledgeRevisionStatus.Conflict : KnowledgeRevisionStatus.Draft,
            basis.KnowledgeId, candidate?.KnowledgeId, candidateVersion, source);
        Save(revision);
        Audit(pending ? "KNOWLEDGE_REVISION_CONFLICT" : "KNOWLEDGE_REVISION_CREATED", revision, actor);
        return revision;
    }

    public IReadOnlyList<KnowledgeRevision> ListRevisions() =>
        Directory.EnumerateFiles(_root, "*.json")
            .Select(path => GetRevision(Path.GetFileNameWithoutExtension(path)))
            .OrderByDescending(revision => revision.CreatedAt).ToArray();

    public KnowledgeRevision GetRevision(string revisionId)
    {
        if (revisionId.Length != 32 || !revisionId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid revision ID.", nameof(revisionId));
        var revision = Read(Path.Combine(_root, revisionId + ".json"));
        return revision with { Status = GetStatus(revision) };
    }

    public KnowledgeRevision Publish(string revisionId, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Operator is required.", nameof(actor));
        using var lease = AcquireLock();
        var revision = GetRevision(revisionId);
        if (revision.Status != KnowledgeRevisionStatus.Approved || revision.CandidateKnowledgeId is null)
            throw new InvalidOperationException("Only an approved revision can be published.");
        try
        {
            _review.PublishReplacement(revision.CandidateKnowledgeId, revision.BaseKnowledgeId,
                revision.BaseVersion, actor);
        }
        catch (InvalidOperationException)
        {
            var conflict = revision with { Status = KnowledgeRevisionStatus.Conflict };
            Save(conflict);
            Audit("KNOWLEDGE_REVISION_CONFLICT", conflict, actor);
            throw;
        }
        var published = revision with { Status = KnowledgeRevisionStatus.Published };
        Save(published);
        Audit("KNOWLEDGE_REVISION_PUBLISHED", published, actor);
        return published;
    }

    private KnowledgeRevisionStatus GetStatus(KnowledgeRevision revision)
    {
        if (revision.Status == KnowledgeRevisionStatus.Conflict) return KnowledgeRevisionStatus.Conflict;
        if (revision.CandidateKnowledgeId is null) return revision.Status;
        var item = _review.Snapshot().SingleOrDefault(value => value.KnowledgeId == revision.CandidateKnowledgeId)
            ?? throw new InvalidDataException("Revision candidate is missing.");
        return item.Status switch
        {
            KnowledgeStatus.Draft => KnowledgeRevisionStatus.Draft,
            KnowledgeStatus.Reviewing => KnowledgeRevisionStatus.Reviewing,
            KnowledgeStatus.Approved => KnowledgeRevisionStatus.Approved,
            KnowledgeStatus.Rejected => KnowledgeRevisionStatus.Rejected,
            KnowledgeStatus.Published => KnowledgeRevisionStatus.Published,
            _ => throw new InvalidDataException("Unexpected knowledge status.")
        };
    }

    private FileStream AcquireLock() => new(Path.Combine(_root, "revision.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private static KnowledgeRevision Read(string path) =>
        JsonSerializer.Deserialize<KnowledgeRevision>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Revision is null.");
    private void Save(KnowledgeRevision revision)
    {
        var path = Path.Combine(_root, revision.RevisionId + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(revision));
        File.Move(temp, path, true);
    }
    private void Audit(string eventName, KnowledgeRevision revision, string actor) =>
        File.AppendAllText(_auditPath, JsonSerializer.Serialize(new
        {
            Event = eventName, revision.RevisionId, revision.SellerId, revision.ProductId,
            revision.BaseVersion, revision.CandidateVersion, revision.BaseKnowledgeId,
            revision.CandidateKnowledgeId, ChangedKeys = revision.ChangedFields.Select(field => field.Key).ToArray(),
            revision.Status, Actor = actor, At = DateTimeOffset.UtcNow
        }) + Environment.NewLine);
}
