using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProductIdStatus { NotFound, LowConfidence, Ambiguous, Identified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProductContextStatus { NotFound, LowConfidence, Ambiguous, Unmapped, Matched }

public sealed record ProductIdCandidate(string ProductId, double? Confidence, IReadOnlyList<int> LineNumbers, string Evidence);
public sealed record ProductIdExtractionResult(ProductIdStatus Status, string? ProductId, double? Confidence,
    IReadOnlyList<ProductIdCandidate> Candidates, string Reason);

public sealed record ProductContext(ProductContextStatus Status, string? ProductId, double? Confidence,
    ProductProfile? Profile, bool IsTestCatalog, string CatalogPath, ProductIdExtractionResult Extraction);
