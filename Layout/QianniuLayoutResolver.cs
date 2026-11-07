using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using OpenCvSharp;
using QnocrPoc.Models;

namespace QnocrPoc.Layout;

public sealed class QianniuLayoutResolver
{
    public ResolvedLayout Resolve(Mat image) => ResolveCore(image,
        UiaGeometryProbe.Unavailable("UIA skipped: in-memory frame"), Stopwatch.StartNew());

    public async Task<ResolvedLayout> ResolveAsync(string screenshotPath, QianniuWindow? window = null)
    {
        var timer = Stopwatch.StartNew();
        using var image = Decode(screenshotPath);
        var geometry = window is null ? UiaGeometryProbe.Unavailable("UIA skipped: no live HWND for this image")
            : await new UiaGeometryProbe().ReadAsync(window, new System.Drawing.Size(image.Width, image.Height));
        return ResolveCore(image, geometry, timer);
    }

    // Deterministic geometry evidence seam: never contacts UIA or reads text.
    public ResolvedLayout ResolveWithGeometry(string screenshotPath, UiaGeometrySnapshot geometry)
    {
        var timer = Stopwatch.StartNew();
        using var image = Decode(screenshotPath);
        return ResolveCore(image, geometry, timer);
    }

    private static Mat Decode(string path)
    {
        var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (!image.Empty()) return image;
        image.Dispose();
        throw new InvalidDataException("Layout input is not a decodable image.");
    }

    private static ResolvedLayout ResolveCore(Mat image, UiaGeometrySnapshot geometry, Stopwatch timer)
    {
        var size = new System.Drawing.Size(image.Width, image.Height);
        if (geometry.Nodes.Count > 0 && geometry.WindowBounds.Size != size)
            geometry = UiaGeometryProbe.Unavailable("UIA geometry frame size mismatch; skipped");
        var ratio = new QianniuLayoutRatio().Resolve(size);
        var diagnostics = new List<string> { geometry.Status, "UIA allowlist: ControlType, BoundingRectangle, IsOffscreen; no Name, Value, TextPattern or control actions." };
        var visual = VisualLayoutLocator.Locate(image, ratio, diagnostics);
        ResolvedRegion Region(string name, Rectangle expected, Rectangle? visualBounds)
        {
            var match = SelectUiaRegion(geometry.Nodes, expected, size);
            if (match is not null)
                return new(match.Bounds, "UIA", $"{match.ControlType}, node={match.Id}; unique geometry match within ratio-guided search bounds");
            diagnostics.Add($"UIA {name}: no unambiguous geometry match; trying visual.");
            return visualBounds is { } bounds ? new(bounds, "Visual", "Long panel separators and coherent light-theme layout")
                : new(expected, "RatioFallback", "No trustworthy UIA/visual layout; V0.5 normalized projection");
        }
        var buyer = Region("BuyerList", ratio.BuyerListRegion, visual?.Layout.BuyerListRegion);
        var chat = Region("Chat", ratio.ChatRegion, visual?.Layout.ChatRegion);
        var product = Region("Product", ratio.ProductRegion, visual is { ProductFooterVerified: true } ? visual.Layout.ProductRegion : null);
        var input = Region("Input", ratio.InputRegion, visual?.Layout.InputRegion);
        var candidate = new QianniuLayout(size, buyer.Bounds, chat.Bounds, product.Bounds, input.Bounds);
        if (!IsCoherent(candidate))
        {
            diagnostics.Add("Mixed candidates overlap or violate layout topology; discarding UIA selections.");
            ResolvedRegion Secondary(Rectangle fallback, Rectangle? bounds) => bounds is { } rectangle
                ? new(rectangle, "Visual", "Coherent layout replacement") : new(fallback, "RatioFallback", "Coherent layout replacement");
            buyer = Secondary(ratio.BuyerListRegion, visual?.Layout.BuyerListRegion);
            chat = Secondary(ratio.ChatRegion, visual?.Layout.ChatRegion);
            product = Secondary(ratio.ProductRegion, visual is { ProductFooterVerified: true } ? visual.Layout.ProductRegion : null);
            input = Secondary(ratio.InputRegion, visual?.Layout.InputRegion);
            if (!IsCoherent(new(size, buyer.Bounds, chat.Bounds, product.Bounds, input.Bounds)))
            {
                diagnostics.Add("Secondary layout is inconsistent; using ratio fallback for all regions.");
                buyer = new(ratio.BuyerListRegion, "RatioFallback", "Inconsistent evidence");
                chat = new(ratio.ChatRegion, "RatioFallback", "Inconsistent evidence");
                product = new(ratio.ProductRegion, "RatioFallback", "Inconsistent evidence");
                input = new(ratio.InputRegion, "RatioFallback", "Inconsistent evidence");
            }
        }
        timer.Stop();
        return new(size, buyer, chat, product, input, geometry, diagnostics, timer.Elapsed.TotalMilliseconds);
    }

    private static UiaLayoutNode? SelectUiaRegion(IReadOnlyList<UiaLayoutNode> nodes, Rectangle expected, System.Drawing.Size size)
    {
        var unique = new List<UiaLayoutNode>();
        foreach (var node in nodes.Where(node => node.ControlType is "ControlType.Pane" or "ControlType.Document" or "ControlType.Window" or "ControlType.Group")
            .Where(node => new Rectangle(System.Drawing.Point.Empty, size).Contains(node.Bounds) && node.Bounds.Width > 0 && node.Bounds.Height > 0)
            .OrderByDescending(node => node.ControlType == "ControlType.Document").ThenBy(node => node.Depth))
        {
            // Nested CEF hosts often report the same surface with 1-3 pixel border differences.
            if (unique.Any(other => Math.Abs(other.Bounds.Left - node.Bounds.Left) <= 3 && Math.Abs(other.Bounds.Top - node.Bounds.Top) <= 3 &&
                Math.Abs(other.Bounds.Right - node.Bounds.Right) <= 3 && Math.Abs(other.Bounds.Bottom - node.Bounds.Bottom) <= 3)) continue;
            unique.Add(node);
        }
        var candidates = unique
            .Where(node => Math.Abs(node.Bounds.Left - expected.Left) <= size.Width * .025 &&
                Math.Abs(node.Bounds.Right - expected.Right) <= size.Width * .025 &&
                Math.Abs(node.Bounds.Top - expected.Top) <= size.Height * .035 &&
                Math.Abs(node.Bounds.Bottom - expected.Bottom) <= size.Height * .07)
            .Select(node =>
            {
                var intersection = Rectangle.Intersect(node.Bounds, expected);
                var area = (double)intersection.Width * intersection.Height;
                var union = (double)expected.Width * expected.Height + (double)node.Bounds.Width * node.Bounds.Height - area;
                return (Node: node, Score: union > 0 ? area / union : 0);
            }).Where(match => match.Score >= .78).OrderByDescending(match => match.Score).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[0].Score - candidates[1].Score < .025) return null;
        return candidates[0].Node;
    }

    private static bool IsCoherent(QianniuLayout layout) =>
        layout.GetRegions().All(region => region.Bounds.Width > 0 && region.Bounds.Height > 0 && new Rectangle(System.Drawing.Point.Empty, layout.ScreenshotSize).Contains(region.Bounds)) &&
        layout.BuyerListRegion.Right <= layout.ChatRegion.Left && layout.ChatRegion.Right <= layout.ProductRegion.Left &&
        layout.ChatRegion.Bottom <= layout.InputRegion.Top && layout.ChatRegion.Left == layout.InputRegion.Left && layout.ChatRegion.Right == layout.InputRegion.Right;

    public static string SaveDiagnostics(ResolvedLayout layout, string screenshotPath, Action<string> log)
    {
        Directory.CreateDirectory("Output/Layout");
        var path = Path.GetFullPath(Path.Combine("Output", "Layout", $"layout_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(new { ScreenshotPath = Path.GetFullPath(screenshotPath), Layout = layout }, new JsonSerializerOptions { WriteIndented = true }));
        log($"[Layout] {layout.ScreenshotSize.Width}x{layout.ScreenshotSize.Height}; {layout.ElapsedMilliseconds:F1} ms");
        foreach (var diagnostic in layout.Diagnostics) log($"[Layout] {diagnostic}");
        foreach (var (name, region) in layout.GetRegions()) log($"[Layout] {name}: {region.Source} Bounds={region.Bounds}; {region.Reason}");
        log($"Layout diagnostics: {path}");
        return path;
    }
}
