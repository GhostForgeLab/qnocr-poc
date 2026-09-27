using System.Text.RegularExpressions;
using QnocrPoc.Models;

namespace QnocrPoc.Product;

/// <summary>Same-capture, labeled product-region OCR only; no fuzzy matching or historical reuse.</summary>
public sealed class OcrProductResolver(ProductIdExtractor? extractor = null) : IProductContextResolver
{
    private readonly ProductIdExtractor _extractor = extractor ?? new ProductIdExtractor();
    private static readonly Regex NameLabel = new(@"^(?:商品名称|商品标题|宝贝名称)[ \t]*[:：][ \t]*(?<name>[^\r\n]+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public BoundProductContext? Resolve(MessageSession session, OcrResult? productOcr,
        DateTimeOffset capturedAt, string captureId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (productOcr is null || string.IsNullOrWhiteSpace(captureId)) return null;
        var result = _extractor.Extract(productOcr);
        if (result.Status != ProductIdStatus.Identified || result.ProductId is null ||
            result.Confidence is not { } confidence || confidence < _extractor.MinimumConfidence ||
            string.IsNullOrWhiteSpace(session.SellerId)) return null;
        var names = productOcr.Lines.Where(line => line.Confidence is { } score && score >= 0.80f)
            .Select(line => NameLabel.Match(line.Text.Trim()))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value.Trim())
            .Where(name => name.Length is > 0 and <= 100 && !name.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal).ToArray();
        var name = names.Length == 1 ? names[0] : "";
        return new BoundProductContext(session.SessionId, session.SellerId,
            result.ProductId, name, ProductContextSource.OCR, confidence, capturedAt,
            "OcrLabeledProductId", captureId);
    }
}
