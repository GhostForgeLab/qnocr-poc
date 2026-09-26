using System.Text.Json;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Exact-ID, single-record, read-only local test source. Never treats OCR association as verified.</summary>
public sealed class ProductKnowledgeProvider : IKnowledgeProvider
{
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    { "Capacity", "Material", "CustomizationPolicy" };
    private readonly ProductKnowledgeRecord _record;

    public ProductKnowledgeProvider(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > 16 * 1024)
            throw new InvalidDataException("Single test product file exceeds 16 KiB.");
        _record = JsonSerializer.Deserialize<ProductKnowledgeRecord>(File.ReadAllText(fullPath),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Test product record is null.");
        if (_record.ProductId != "1051087533910" || _record.Source != "LocalTestProductConfig" ||
            !_record.IsTestData || _record.UpdatedAt == default || _record.Fields is null ||
            _record.Fields.Count is < 1 or > 3 || _record.Fields.Keys.Any(key => !AllowedFields.Contains(key)) ||
            _record.Fields.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
                value.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '{' or '}')))
            throw new InvalidDataException("Only the bounded single-product test record and approved fields are accepted.");
    }

    public Task<KnowledgeResult> GetAsync(KnowledgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProductReference?.ProductId != _record.ProductId || request.NeedFields.Count == 0)
            return Task.FromResult(new KnowledgeResult(false, "None", null, "",
                request.NeedFields.Distinct(StringComparer.Ordinal).ToArray()));
        var returned = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var field in request.NeedFields.Distinct(StringComparer.Ordinal))
            if (_record.Fields.TryGetValue(field, out var value)) returned[field] = value;
            else missing.Add(field);
        var complete = missing.Count == 0 && returned.Count > 0;
        var content = complete ? string.Join("\n", returned.Select(pair => pair.Key + ": " + pair.Value)) : "";
        return Task.FromResult(new KnowledgeResult(complete, complete ? _record.Source : "None", null,
            content, missing, returned, complete ? _record.UpdatedAt : null));
    }
}
