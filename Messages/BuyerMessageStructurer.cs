using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OpenCvSharp;
using QnocrPoc.Models;
using QnocrPoc.Product;

namespace QnocrPoc.Messages;

/// <summary>Conservative light-theme, left-buyer layout rules; never reads UIA text or invents timestamps.</summary>
public sealed class BuyerMessageStructurer
{
    private static readonly Regex TimestampPattern = new(@"(?<![0-9])(?<y>[0-9]{4})-(?<m>[0-9]{1,2})-(?<d>[0-9]{1,2})\s+(?<h>[0-9]{1,2}):(?<min>[0-9]{2}):(?<s>[0-9]{2})(?![0-9])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TextContent = new(@"[\p{L}\p{N}]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public BuyerMessageBatch Structure(OcrResult ocr, string productId = "", string productBinding = "None")
    {
        ArgumentNullException.ThrowIfNull(ocr);
        if (productId != "" && !ProductIdExtractor.IsValidProductId(productId)) throw new ArgumentException("Invalid product context ID.", nameof(productId));
        var bytes = File.ReadAllBytes(ocr.ImagePath);
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("Message source image cannot be decoded.");
        using var mask = new Mat();
        Cv2.InRange(image, new Scalar(250, 250, 250), new Scalar(255, 255, 255), mask);
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var bubbles = contours.Select(contour => (Bounds: Cv2.BoundingRect(contour), Area: Cv2.ContourArea(contour)))
            .Where(item => item.Bounds.Left >= image.Width * .09 && item.Bounds.Left <= image.Width * .30 &&
                item.Bounds.Right <= image.Width * .92 && item.Bounds.Width >= 18 && item.Bounds.Height >= 20 &&
                item.Area >= item.Bounds.Width * (double)item.Bounds.Height * .65)
            .Select(item => item.Bounds).OrderBy(bounds => bounds.Top).ThenBy(bounds => bounds.Left).ToArray();
        var messages = new List<BuyerMessage>();
        var evidence = new List<BuyerMessageEvidence>();
        var review = new List<MessageReviewItem>();
        var consumed = new HashSet<int>();

        foreach (var bubble in bubbles)
        {
            var body = ocr.Lines.Where(line => BelongsTo(line.Bounds, bubble)).OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToArray();
            if (body.Length == 0) continue;
            foreach (var line in body) consumed.Add(line.Number);
            var text = string.Join("\n", body.Select(line => line.Text));
            var numbers = body.Select(line => line.Number).ToArray();
            string? reason = null;
            if (bubble.Top <= 1 || bubble.Bottom >= image.Height - 1) reason = "ClippedBubble";
            else if (body.Any(line => !Reliable(line))) reason = "LowConfidenceBody";
            else if (!TextContent.IsMatch(text)) reason = "NonTextOrPunctuationOnly";
            var header = reason is null ? FindHeader(ocr.Lines, bubble, bubbles, image) : null;
            if (reason is null && header is null) reason = "MissingReliableFullTimestampAndBuyerHeader";
            if (reason is not null)
            {
                review.Add(new(text, reason, numbers, Bounds(bubble)));
                continue;
            }
            foreach (var line in header!.Value.Lines) consumed.Add(line.Number);
            messages.Add(new BuyerMessage { Text = text, Timestamp = header.Value.Time, ProductId = productId });
            evidence.Add(new(messages.Count - 1, Bounds(bubble), numbers, header.Value.Lines.Select(line => line.Number).Distinct().ToArray(),
                body.Concat(header.Value.Lines).Min(line => (double)line.Confidence!.Value)));
        }

        // Keep all unassigned OCR evidence review-only, including uncertain headers/status and other roles.
        foreach (var line in ocr.Lines.Where(line => !consumed.Contains(line.Number) && !string.IsNullOrWhiteSpace(line.Text)))
        {
            var reason = !Reliable(line) ? "LowConfidenceUnassignedOcr" : IsGrayHeader(line.Bounds, image)
                ? "UnassignedHeaderOrStatus" : "NotConfirmedBuyerBubbleOrRole";
            review.Add(new(line.Text, reason, [line.Number], line.Bounds));
        }
        return new(Path.GetFullPath(ocr.ImagePath), Convert.ToHexString(SHA256.HashData(bytes)), messages, evidence, review,
            productId == "" ? "None" : productBinding,
            "Timestamp is the full date/time displayed in the screenshot (DateTimeKind.Unspecified); no capture-time substitution or timezone inference.");
    }

    private static (DateTime Time, OcrLine[] Lines)? FindHeader(IReadOnlyList<OcrLine> lines, Rect bubble, Rect[] bubbles, Mat image)
    {
        var candidates = new List<(DateTime Time, OcrLine[] Lines, int Bottom)>();
        foreach (var line in lines)
        {
            if (!Reliable(line) || !IsGrayHeader(line.Bounds, image) || bubbles.Any(bounds => BelongsTo(line.Bounds, bounds))) continue;
            var bottom = (long)line.Bounds.Y + line.Bounds.Height;
            if (bottom > bubble.Top || bubble.Top - bottom > Math.Max(32, line.Bounds.Height * 3L)) continue;
            if (bubbles.Any(other => other != bubble && other.Top >= bottom && other.Bottom <= bubble.Top)) continue;
            var normalized = NormalizeWidth(line.Text);
            var matches = TimestampPattern.Matches(normalized);
            if (matches.Count != 1) continue;
            var match = matches[0];
            DateTime time;
            try
            {
                int Part(string name) => int.Parse(match.Groups[name].Value, System.Globalization.CultureInfo.InvariantCulture);
                time = new DateTime(Part("y"), Part("m"), Part("d"), Part("h"), Part("min"), Part("s"), DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException) { continue; }
            var prefix = normalized[..match.Index].Trim();
            if (prefix.Length > 0 && Math.Abs(line.Bounds.X - bubble.Left) <= image.Width * .06)
            {
                candidates.Add((time, [line], (int)bottom));
                continue;
            }
            var names = lines.Where(name => name.Number != line.Number && Reliable(name) &&
                !TimestampPattern.IsMatch(NormalizeWidth(name.Text)) && IsGrayHeader(name.Bounds, image) &&
                !bubbles.Any(bounds => BelongsTo(name.Bounds, bounds)) &&
                Math.Abs(name.Bounds.X - bubble.Left) <= image.Width * .06 &&
                // OCR's rotated bounding rectangles can overlap although the header glyphs do not.
                // Permit at most half a line height; keep the same-row, gray, unique-name checks.
                name.Bounds.X + (long)name.Bounds.Width <= line.Bounds.X + Math.Max(3, Math.Max(name.Bounds.Height, line.Bounds.Height) * .5) &&
                line.Bounds.X - (name.Bounds.X + (long)name.Bounds.Width) <= Math.Max(name.Bounds.Height, line.Bounds.Height) * 2L &&
                Math.Abs(name.Bounds.Y + name.Bounds.Height / 2d - line.Bounds.Y - line.Bounds.Height / 2d) <= Math.Max(name.Bounds.Height, line.Bounds.Height) * .5).ToArray();
            if (names.Length == 1) candidates.Add((time, [names[0], line], (int)bottom));
        }
        if (candidates.Count == 0) return null;
        var nearest = candidates.Max(candidate => candidate.Bottom);
        var row = candidates.Where(candidate => nearest - candidate.Bottom <= 3).ToArray();
        if (row.Select(candidate => candidate.Time).Distinct().Count() != 1) return null;
        return (row[0].Time, row[0].Lines);
    }

    private static bool Reliable(OcrLine line) => line.Confidence is { } confidence && float.IsFinite(confidence) && confidence is >= .80f and <= 1;
    private static OcrBounds Bounds(Rect bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    private static bool BelongsTo(OcrBounds line, Rect bubble)
    {
        if (line.Width <= 0 || line.Height <= 0) return false;
        var left = Math.Max((long)line.X, bubble.Left);
        var top = Math.Max((long)line.Y, bubble.Top);
        var right = Math.Min(line.X + (long)line.Width, bubble.Right);
        var bottom = Math.Min(line.Y + (long)line.Height, bubble.Bottom);
        return right > left && bottom > top && (right - left) * (double)(bottom - top) >= line.Width * (double)line.Height * .80;
    }
    private static bool IsGrayHeader(OcrBounds bounds, Mat image)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < 0 || bounds.Y < 0 ||
            bounds.X + (long)bounds.Width > image.Width || bounds.Y + (long)bounds.Height > image.Height) return false;
        var gray = 0;
        var dark = 0;
        for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
        {
            var pixel = image.At<Vec3b>(y, x);
            var minimum = Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
            var maximum = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2));
            if (maximum < 80) dark++;
            else if (minimum >= 80 && maximum <= 205 && maximum - minimum <= 20) gray++;
        }
        return gray >= 10 && (double)dark / (gray + dark) < .15;
    }
    private static string NormalizeWidth(string text) => new(text.Select(c => c is >= '\uFF01' and <= '\uFF5E' ? (char)(c - 0xFEE0) : c).ToArray());
}
