using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using QnocrPoc.Models;

namespace QnocrPoc.Layout;

/// <summary>Light-theme panel separators only. No OCR, text matching, or model inference.</summary>
internal sealed class VisualLayoutLocator
{
    internal sealed record Evidence(QianniuLayout Layout, bool ProductFooterVerified);

    public static Evidence? Locate(Mat image, QianniuLayout hint, List<string> diagnostics)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++) Marshal.Copy(image.Ptr(y), pixels, y * width * 3, width * 3);
        int Delta(int x1, int y1, int x2, int y2)
        {
            var p = (y1 * width + x1) * 3;
            var q = (y2 * width + x2) * 3;
            return Math.Max(Math.Abs(pixels[p] - pixels[q]), Math.Max(Math.Abs(pixels[p + 1] - pixels[q + 1]), Math.Abs(pixels[p + 2] - pixels[q + 2])));
        }
        var neutral = 0;
        var samples = 0;
        for (var y = hint.ChatRegion.Top; y < hint.ChatRegion.Bottom; y += 7)
        for (var x = hint.ChatRegion.Left; x < hint.ChatRegion.Right; x += 7)
        {
            var p = (y * width + x) * 3;
            var low = Math.Min(pixels[p], Math.Min(pixels[p + 1], pixels[p + 2]));
            var high = Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2]));
            if (low >= 200 && high - low <= 18) neutral++;
            samples++;
        }
        if ((double)neutral / samples < 0.70)
        {
            diagnostics.Add("Visual rejected: insufficient light neutral panel background (unsupported theme/content).");
            return null;
        }

        int? Edge(string name, bool vertical, int preferred, int start, int end, int spanStart, int spanEnd, double minimumCoverage)
        {
            var extent = vertical ? width : height;
            var spanExtent = vertical ? height : width;
            start = Math.Clamp(start, 1, extent - 1);
            end = Math.Clamp(end, start, extent - 1);
            spanStart = Math.Clamp(spanStart, 0, spanExtent - 1);
            spanEnd = Math.Clamp(spanEnd, spanStart + 1, spanExtent);
            var best = -1;
            var bestScore = double.NegativeInfinity;
            var bestCoverage = 0d;
            for (var at = start; at <= end; at++)
            {
                var changed = 0;
                var count = 0;
                var magnitude = 0d;
                for (var along = spanStart; along < spanEnd; along += 2)
                {
                    var difference = vertical ? Delta(at - 1, along, at, along) : Delta(along, at - 1, along, at);
                    if (difference > 3) changed++;
                    magnitude += Math.Min(difference, 50);
                    count++;
                }
                var coverage = (double)changed / count;
                if (coverage < minimumCoverage) continue;
                var score = coverage + magnitude / count / 250 - 0.08 * Math.Abs(at - preferred) / Math.Max(1, end - start);
                if (score <= bestScore) continue;
                (best, bestScore, bestCoverage) = (at, score, coverage);
            }
            diagnostics.Add(best < 0 ? $"Visual {name}: no supported long edge" : $"Visual {name}: pixel={best}, edge coverage={bestCoverage:F3}");
            return best < 0 ? null : best;
        }

        var yStart = (int)(height * 0.24);
        var yEnd = (int)(height * 0.85);
        var left = Edge("buyer left", true, hint.BuyerListRegion.Left, hint.BuyerListRegion.Left - (int)(width * .035), hint.BuyerListRegion.Left + (int)(width * .035), yStart, yEnd, .70);
        var center = Edge("buyer/chat separator", true, hint.ChatRegion.Left, hint.ChatRegion.Left - (int)(width * .055), hint.ChatRegion.Left + (int)(width * .055), yStart, yEnd, .70);
        var right = Edge("chat/product separator", true, hint.ProductRegion.Left, hint.ProductRegion.Left - (int)(width * .055), hint.ProductRegion.Left + (int)(width * .055), yStart, yEnd, .65);
        if (left is null || center is null || right is null || !(left < center && center < right)) return null;
        var panelTop = Edge("panel top", false, hint.BuyerListRegion.Top, hint.BuyerListRegion.Top - (int)(height * .07), hint.BuyerListRegion.Top + (int)(height * .07), left.Value + 2, center.Value - 2, .90);
        if (panelTop is null) return null;
        var margin = (int)((right - center) * .04);
        var chatTop = Edge("chat header separator", false, hint.ChatRegion.Top,
            panelTop.Value + (int)(height * .02), panelTop.Value + (int)(height * .13), center.Value + margin, right.Value - margin, .82);
        var editorTop = Edge("message/editor separator", false, hint.InputRegion.Top,
            hint.InputRegion.Top - (int)(height * .16), hint.InputRegion.Top + (int)(height * .16), center.Value + margin, right.Value - margin, .82);
        var productBottom = Edge("product/footer separator", false, hint.ProductRegion.Bottom,
            (int)(height * .86), (int)(height * .98), right.Value + 6, width - 6, .88);
        if (chatTop is null || editorTop is null || editorTop - chatTop < height * .20 || height - editorTop < height * .10)
        {
            diagnostics.Add("Visual layout rejected: missing/implausible horizontal separators.");
            return null;
        }
        if (productBottom is null) diagnostics.Add("Visual Product: footer unavailable; this region requires UIA or ratio fallback.");
        return new(new(new System.Drawing.Size(width, height),
            Rectangle.FromLTRB(left.Value, panelTop.Value, center.Value, height),
            Rectangle.FromLTRB(center.Value, chatTop.Value + 1, right.Value, editorTop.Value),
            Rectangle.FromLTRB(right.Value, panelTop.Value, width, productBottom ?? height),
            Rectangle.FromLTRB(center.Value, editorTop.Value, right.Value, height)), productBottom is not null);
    }
}
