using System.Text.Json;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Small, bounded local test-file repository. Loaded once; no database, watch, import, or mutation.</summary>
public sealed class JsonProductKnowledgeRepository : IProductKnowledgeRepository
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    { "Capacity", "Material", "CustomizationPolicy" };
    private readonly Dictionary<(string SellerId, string ProductId), ProductKnowledgeRecord> _records = new();
    private sealed record ProductFile(List<ProductKnowledgeRecord> Products);

    public JsonProductKnowledgeRepository(string path)
    {
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
                record.Fields is null || record.Fields.Count is < 1 or > 3 ||
                record.Fields.Keys.Any(key => !AllowedFields.Contains(key)) ||
                record.Fields.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
                    value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}')))
                throw new InvalidDataException("Repository accepts only bounded, store-scoped local test data.");
            if (!_records.TryAdd((record.SellerId, record.ProductId), record))
                throw new InvalidDataException("Duplicate SellerId/ProductId in test source.");
        }
    }

    public Task<ProductKnowledgeRecord?> GetAsync(string sellerId, string productId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_records.TryGetValue((sellerId, productId), out var record))
            return Task.FromResult<ProductKnowledgeRecord?>(null);
        return Task.FromResult<ProductKnowledgeRecord?>(record with
        { Fields = new Dictionary<string, string>(record.Fields, StringComparer.Ordinal) });
    }
}
