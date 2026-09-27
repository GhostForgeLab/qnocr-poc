using System.Text.Json;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Bounded test repository; legacy records are cached, review state is re-read per lookup.</summary>
public sealed class JsonProductKnowledgeRepository : IProductKnowledgeRepository
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    { "Capacity", "Material", "CustomizationPolicy" };
    private readonly Dictionary<(string SellerId, string ProductId), ProductKnowledgeRecord> _records = new();
    private readonly string? _workflowItemsPath;
    private sealed record ProductFile(List<ProductKnowledgeRecord> Products);

    public JsonProductKnowledgeRepository(string path, string? workflowItemsPath = null)
    {
        _workflowItemsPath = workflowItemsPath is null ? null : Path.GetFullPath(workflowItemsPath);
        var fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > 64 * 1024)
            throw new InvalidDataException("Test product source exceeds 64 KiB.");
        var json = File.ReadAllText(fullPath);
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        using var document = JsonDocument.Parse(json);
        var records = document.RootElement.TryGetProperty("Products", out _)
            ? JsonSerializer.Deserialize<ProductFile>(json, options)?.Products
            : [JsonSerializer.Deserialize<ProductKnowledgeRecord>(json, options)
                ?? throw new InvalidDataException("Legacy test product record is null.")];
        if (records is null || records.Count is < 1 or > 32)
            throw new InvalidDataException("Only 1–32 explicit local test records are supported.");
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.SellerId) || record.SellerId.Length > 64 ||
                !record.SellerId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
                string.IsNullOrWhiteSpace(record.ProductId) || record.ProductId.Length > 32 ||
                !record.ProductId.All(char.IsAsciiDigit) ||
                string.IsNullOrWhiteSpace(record.Version) || record.Version.Length > 64 ||
                record.Source != "LocalTestProductConfig" || !record.IsTestData || record.UpdatedAt == default ||
                record.Fields is null || record.Fields.Count > 3 ||
                record.Fields.Count == 0 && record.Profile is null ||
                record.Fields.Keys.Any(key => !AllowedFields.Contains(key)) ||
                record.Fields.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
                    value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}')))
                throw new InvalidDataException("Repository accepts only bounded, store-scoped local test data.");
            if (record.Profile is not null)
                ProductKnowledgeSchemaValidator.Validate(record.Profile, record);
            if (!_records.TryAdd((record.SellerId, record.ProductId), record))
                throw new InvalidDataException("Duplicate SellerId/ProductId in test source.");
        }
    }

    /// <summary>Explicit audit lookup, including superseded published versions; never used by the AI provider.</summary>
    public async Task<KnowledgeItem?> GetPublishedVersionAsync(string sellerId, string productId,
        string key, string version, CancellationToken cancellationToken = default)
    {
        _ = await GetAsync(sellerId, productId, cancellationToken); // validates the workflow file first
        if (_workflowItemsPath is null) return null;
        var items = JsonSerializer.Deserialize<List<KnowledgeItem>>(File.ReadAllText(_workflowItemsPath))
            ?? throw new InvalidDataException("Workflow state is null.");
        return items.SingleOrDefault(item => item.SellerId == sellerId && item.ProductId == productId &&
            item.Key == key && item.Version.Version == version && item.Status == KnowledgeStatus.Published);
    }
    public Task<ProductKnowledgeRecord?> GetAsync(string sellerId, string productId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue((sellerId, productId), out var record);
        if (record is null && _workflowItemsPath is null)
            return Task.FromResult<ProductKnowledgeRecord?>(null);
        IReadOnlyList<KnowledgeItem>? published = _workflowItemsPath is null ? null : [];
        KnowledgeItem[] matching = [];
        if (_workflowItemsPath is not null)
        {
            if (!File.Exists(_workflowItemsPath))
                throw new InvalidDataException("Knowledge workflow state is missing; legacy facts are not a fallback.");
            if (new FileInfo(_workflowItemsPath).Length > 256 * 1024)
                throw new InvalidDataException("Workflow state exceeds 256 KiB.");
            var items = JsonSerializer.Deserialize<List<KnowledgeItem>>(File.ReadAllText(_workflowItemsPath))
                ?? throw new InvalidDataException("Workflow state is null.");
            if (items.Count > 256 || items.Select(item => item.KnowledgeId).Distinct(StringComparer.Ordinal).Count() != items.Count ||
                items.Any(item => item.Version is null || item.Source is null ||
                item.Version.Status != item.Status || item.CreatedAt == default ||
                item.UpdatedAt == default || item.Version.CreatedAt == default ||
                string.IsNullOrWhiteSpace(item.KnowledgeId) ||
                string.IsNullOrWhiteSpace(item.Version.Version) || string.IsNullOrWhiteSpace(item.Key) ||
                string.IsNullOrWhiteSpace(item.Category) || string.IsNullOrWhiteSpace(item.Value) ||
                item.Value.Length > 200 || item.Value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}') ||
                item.Source.CapturedAt == default || string.IsNullOrWhiteSpace(item.Source.Reference) ||
                !Enum.IsDefined(item.Source.SourceType) || !item.IsTestData ||
                string.IsNullOrWhiteSpace(item.SellerId) || item.SellerId.Length > 64 ||
                !item.SellerId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
                string.IsNullOrWhiteSpace(item.ProductId) || item.ProductId.Length > 32 ||
                !item.ProductId.All(char.IsAsciiDigit)))
                throw new InvalidDataException("Invalid bounded workflow state.");
            matching = items.Where(item => item.SellerId == sellerId && item.ProductId == productId).ToArray();
            // Explicit workflow mode never falls back to unreviewed legacy facts, even for an empty product.
            published = matching.Where(item => item.Status == KnowledgeStatus.Published &&
                item.AiUseAllowed && !string.IsNullOrWhiteSpace(item.Version.ApprovedBy) &&
                !string.IsNullOrWhiteSpace(item.ReviewedBy) && item.ReviewedAt is not null).ToArray();
            if (published.GroupBy(item => item.Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new InvalidDataException("More than one published item defines a field.");
        }
        if (record is null && matching.Length == 0)
            return Task.FromResult<ProductKnowledgeRecord?>(null);
        record ??= new ProductKnowledgeRecord(productId, new Dictionary<string, string>(),
            "LocalTestProductConfig", matching.Max(item => item.UpdatedAt), true, sellerId,
            matching[^1].Version.Version);
        return Task.FromResult<ProductKnowledgeRecord?>(record with
        {
            Fields = new Dictionary<string, string>(record.Fields, StringComparer.Ordinal),
            Profile = record.Profile is null ? null :
                JsonSerializer.Deserialize<ProductKnowledgeProfile>(JsonSerializer.Serialize(record.Profile)),
            KnowledgeItems = published
        });
    }
}
