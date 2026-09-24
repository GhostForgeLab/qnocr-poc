using System.Text.RegularExpressions;
using QnocrPoc.Models;

namespace QnocrPoc.Product;

/// <summary>Conservative labeled-ID extraction. No fuzzy matching or O/0, I/1 correction.</summary>
public sealed class ProductIdExtractor
{
    // POC policy, not a claim about the complete platform ID specification.
    public const int MinimumIdLength = 10;
    public const int MaximumIdLength = 16;
    public double MinimumConfidence { get; }
    private const string Label = @"(?:(?:商品|宝贝|产品)[ \t]*(?:ID|编号)|(?<![\p{L}\p{N}_])(?:item_id|itemid|ID))";
    private static readonly Regex LabeledNumber = Pattern(Label + @"[ \t]*[:#]?[ \t]*(?<id>[0-9]+(?:[ \t]+[0-9]+)*)(?=$|[\s,;|/()\[\]{}，；、。】])");
    private static readonly Regex LabelOnly = Pattern(@"^" + Label + @"[ \t]*[:#]?[ \t]*$");
    private static readonly Regex OtherIdentifier = Pattern(@"订单|交易|会员|用户|买家|卖家|店铺|物流|运单|快递|手机|电话|order|trade|user|buyer|seller|shop|sku");

    public ProductIdExtractor(double minimumConfidence = 0.80)
    {
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        MinimumConfidence = minimumConfidence;
    }

    public ProductIdExtractionResult Extract(OcrResult ocr)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        var candidates = new List<ProductIdCandidate>();
        foreach (var line in ocr.Lines)
        {
            var text = NormalizeWidth(line.Text).Trim();
            foreach (Match match in LabeledNumber.Matches(text))
            {
                var id = match.Groups["id"].Value;
                if (!IsValidProductId(id)) continue;
                var explicitProductLabel = match.Value.StartsWith("商品", StringComparison.Ordinal) ||
                    match.Value.StartsWith("宝贝", StringComparison.Ordinal) || match.Value.StartsWith("产品", StringComparison.Ordinal);
                var prefix = text[..match.Index];
                var clause = prefix.Split([',', ';', '|', '，', '；']).Last();
                if (!explicitProductLabel && (OtherIdentifier.IsMatch(clause) || HasOtherLabelToLeft(line, ocr.Lines))) continue;
                candidates.Add(new(id, Score(line.Confidence), [line.Number], line.Text));
            }

            // Common OCR segmentation: a stand-alone ID label and digits in a separate box on the SAME row.
            if (!LabelOnly.IsMatch(text) || HasOtherLabelToLeft(line, ocr.Lines)) continue;
            foreach (var numeric in ocr.Lines.Where(other => !ReferenceEquals(other, line)))
            {
                var id = NormalizeWidth(numeric.Text).Trim();
                if (!IsValidProductId(id) || !ImmediatelyRight(line.Bounds, numeric.Bounds)) continue;
                var labelScore = Score(line.Confidence);
                var numberScore = Score(numeric.Confidence);
                var score = labelScore is { } a && numberScore is { } b ? Math.Min(a, b) : (double?)null;
                candidates.Add(new(id, score, [line.Number, numeric.Number], line.Text + " | " + numeric.Text));
            }
        }

        var distinct = candidates.GroupBy(candidate => candidate.ProductId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First()).ToArray();
        if (distinct.Length == 0) return new(ProductIdStatus.NotFound, null, null, distinct, "No labeled, contiguous 10-16 digit product ID found.");
        if (distinct.Length > 1) return new(ProductIdStatus.Ambiguous, null, null, distinct, "Multiple distinct product IDs; no automatic card selection.");
        var candidate = distinct[0];
        if (candidate.Confidence is null || candidate.Confidence < MinimumConfidence)
            return new(ProductIdStatus.LowConfidence, null, candidate.Confidence, distinct, "ID evidence is below the minimum OCR confidence or has no valid score.");
        return new(ProductIdStatus.Identified, candidate.ProductId, candidate.Confidence, distinct,
            "Unique labeled ID. Confidence is supporting OCR line score, not a verified correctness probability.");
    }

    public static bool IsValidProductId(string? value) => value is not null && value.Length is >= MinimumIdLength and <= MaximumIdLength &&
        value.All(c => c is >= '0' and <= '9') && value.Any(c => c != '0');

    private static double? Score(float? value) => value is { } score && float.IsFinite(score) && score is >= 0 and <= 1 ? score : null;
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string NormalizeWidth(string text) => new(text.Select(c => c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c == '\u3000' ? ' ' : c).ToArray());
    private static bool ImmediatelyRight(OcrBounds left, OcrBounds right)
    {
        if (left.Width <= 0 || left.Height <= 0 || right.Width <= 0 || right.Height <= 0) return false;
        var gap = (long)right.X - left.X - left.Width;
        var height = Math.Max(left.Height, right.Height);
        return gap >= -2 && gap <= height * 4L && Math.Abs((double)left.Y + left.Height / 2d - right.Y - right.Height / 2d) <= height * .5;
    }
    private static bool HasOtherLabelToLeft(OcrLine label, IReadOnlyList<OcrLine> lines) =>
        lines.Any(other => !ReferenceEquals(other, label) && OtherIdentifier.IsMatch(NormalizeWidth(other.Text)) && ImmediatelyRight(other.Bounds, label.Bounds));
}
