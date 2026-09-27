using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

/// <summary>Parses one explicit test JSON into an untrusted Draft; never reviews or publishes.</summary>
public sealed class KnowledgeImportService
{
    private readonly KnowledgeAuthoringService _authoring;
    private readonly string _auditPath;
    private readonly string _receiptsDirectory;
    private sealed record ImportReceipt(string ImportId, string SellerId, string ProductId,
        string RawHash, string DraftId);
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public KnowledgeImportService(string directory, KnowledgeAuthoringService authoring)
    {
        _authoring = authoring ?? throw new ArgumentNullException(nameof(authoring));
        Directory.CreateDirectory(directory);
        _auditPath = Path.Combine(Path.GetFullPath(directory), "import-audit.jsonl");
        _receiptsDirectory = Path.Combine(Path.GetFullPath(directory), "imports");
        Directory.CreateDirectory(_receiptsDirectory);
    }

    public KnowledgeDraft Import(KnowledgeImportRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ImportId.Length != 32 || !request.ImportId.All(Uri.IsHexDigit) ||
            request.CreatedAt == default || request.Source is null ||
            request.Source.SourceType != KnowledgeSourceType.Import ||
            string.IsNullOrWhiteSpace(request.RawData) || request.RawData.Length > 16 * 1024)
            throw new InvalidDataException("Only one bounded, attributed test JSON import is supported.");
        using var lease = new FileStream(Path.Combine(_receiptsDirectory, "import.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RawData)));
        var receiptPath = Path.Combine(_receiptsDirectory, request.ImportId + ".json");
        if (File.Exists(receiptPath))
        {
            var existing = JsonSerializer.Deserialize<ImportReceipt>(File.ReadAllText(receiptPath))
                ?? throw new InvalidDataException("Import receipt is null.");
            if (existing.SellerId != request.SellerId || existing.ProductId != request.ProductId ||
                existing.RawHash != hash)
                throw new InvalidOperationException("ImportId was reused for different input.");
            return _authoring.GetDraft(existing.DraftId);
        }
        var payload = JsonSerializer.Deserialize<KnowledgeImportPayload>(request.RawData, Options)
            ?? throw new InvalidDataException("Import payload is null.");
        if (!payload.IsTestData)
            throw new InvalidDataException("Real or bulk product import is outside this PoC.");
        var input = new ProductKnowledgeAuthoringInput(request.SellerId, request.ProductId,
            payload.Name, payload.Version, request.Source, true,
            payload.Capacity is null ? null : [payload.Capacity], payload.Material,
            payload.Features is null ? null : [new ProductFeature(payload.Features)]);
        var draft = _authoring.Create(input, actor);
        var receipt = new ImportReceipt(request.ImportId, request.SellerId, request.ProductId,
            hash, draft.DraftId);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        File.AppendAllText(_auditPath, JsonSerializer.Serialize(new
        {
            Event = "KNOWLEDGE_IMPORTED_AS_DRAFT", request.ImportId, request.SellerId,
            request.ProductId, draft.DraftId, draft.Status, Actor = actor,
            At = DateTimeOffset.UtcNow
        }) + Environment.NewLine);
        return draft;
    }
}
