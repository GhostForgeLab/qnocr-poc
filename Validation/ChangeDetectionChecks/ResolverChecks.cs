using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Detection;
using QnocrPoc.Layout;
using QnocrPoc.Models;

internal static class ResolverChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV06"));
        Directory.CreateDirectory(root);
        var size = new Size(1000, 800);
        string Fixture(string name, bool separators, bool dark = false, int header = 147, int editor = 520)
        {
            var path = Path.Combine(root, name + ".png");
            using var image = new Bitmap(size.Width, size.Height);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(dark ? Color.Black : Color.White);
                if (separators)
                {
                    using var blue = new SolidBrush(Color.FromArgb(63, 125, 255));
                    using var buyer = new SolidBrush(Color.FromArgb(246, 247, 249));
                    using var chat = new SolidBrush(Color.FromArgb(240, 242, 245));
                    using var product = new SolidBrush(Color.FromArgb(250, 250, 250));
                    graphics.FillRectangle(blue, 0, 0, 49, 800);
                    graphics.FillRectangle(buyer, 49, 100, 183, 700);
                    graphics.FillRectangle(chat, 232, 100, 399, 700);
                    graphics.FillRectangle(product, 631, 100, 369, 648);
                    graphics.DrawLine(Pens.LightGray, 248, header, 615, header);
                    graphics.DrawLine(Pens.LightGray, 248, editor, 615, editor);
                }
            }
            image.Save(path, ImageFormat.Png);
            return path;
        }
        var plain = Fixture("blank", false);
        var dark = Fixture("dark", false, true);
        var visualPath = Fixture("separators", true);
        var resolver = new QianniuLayoutResolver();
        var unavailable = UiaGeometryProbe.Unavailable("Test: no HWND");
        var ratio = new QianniuLayoutRatio().Resolve(size);
        var fallback = resolver.ResolveWithGeometry(plain, unavailable);
        check(fallback.GetRegions().All(pair => pair.Region.Source == "RatioFallback") && fallback.ToPixelLayout() == ratio,
            "blank image with unavailable UIA returns exact V0.5 fallback");
        check(resolver.ResolveWithGeometry(dark, unavailable).GetRegions().All(pair => pair.Region.Source == "RatioFallback"),
            "unsupported dark appearance returns explicit ratio fallback");
        var visual = resolver.ResolveWithGeometry(visualPath, unavailable);
        check(visual.GetRegions().All(pair => pair.Region.Source == "Visual") && visual.Chat.Bounds.Top == 148 &&
            visual.Chat.Bounds.Bottom is >= 520 and <= 521 && visual.Chat.Bounds.Left == 232 && visual.Chat.Bounds.Right == 631,
            "long visual separators locate chat without including the header (known synthetic geometry)");
        check(visual.Product.Bounds.Bottom == 748 && visual.BuyerList.Bounds.Top == 100 && visual.Input.Bounds.Top == visual.Chat.Bounds.Bottom,
            "visual buyer/product/input boundaries match synthetic panel geometry");

        var nodes = visual.GetRegions().Select((pair, index) => new UiaLayoutNode(index + 1, 0, 1, "ControlType.Pane", pair.Region.Bounds)).ToArray();
        var evidence = new UiaGeometrySnapshot(true, "Test geometry", new Rectangle(500, 200, size.Width, size.Height), nodes);
        var uia = resolver.ResolveWithGeometry(visualPath, evidence);
        check(uia.GetRegions().All(pair => pair.Region.Source == "UIA"), "UIA geometry takes precedence over available visual features");
        check(uia.Chat.Bounds == visual.Chat.Bounds, "UIA local image coordinates remain unchanged (no double screen-origin subtraction)");
        var limited = resolver.ResolveWithGeometry(visualPath, evidence with { Complete = false, Status = "Test limited traversal" });
        check(limited.GetRegions().All(pair => pair.Region.Source == "UIA") && !limited.Uia.Complete,
            "validated nodes from limited UIA traversal remain usable and limitation stays in diagnostics");
        var partial = resolver.ResolveWithGeometry(visualPath, evidence with { Nodes = [nodes[2]] });
        check(partial.Product.Source == "UIA" && partial.Chat.Source == "Visual", "per-region priority preserves visual chat with UIA-only product panel");
        var duplicates = nodes.Concat(nodes.Select(node => node with { Id = node.Id + 10, Depth = 2, Bounds = new Rectangle(node.Bounds.X + 1, node.Bounds.Y + 1, node.Bounds.Width, node.Bounds.Height) })).ToArray();
        check(resolver.ResolveWithGeometry(visualPath, evidence with { Nodes = duplicates }).Chat.Source == "UIA",
            "near-identical nested CEF hosts are deduplicated");
        var chatHint = ratio.ChatRegion;
        var ambiguous = new[]
        {
            new UiaLayoutNode(1, 0, 1, "ControlType.Pane", new(chatHint.X, chatHint.Y - 5, chatHint.Width, chatHint.Height)),
            new UiaLayoutNode(2, 0, 1, "ControlType.Pane", new(chatHint.X, chatHint.Y + 5, chatHint.Width, chatHint.Height))
        };
        check(resolver.ResolveWithGeometry(visualPath, evidence with { Nodes = ambiguous }).Chat.Source == "Visual",
            "equally plausible distinct UIA panels fall back to visual");
        check(resolver.ResolveWithGeometry(visualPath, evidence with { WindowBounds = new Rectangle(0, 0, 999, 800) }).Chat.Source == "Visual",
            "stale UIA screenshot dimensions are rejected");
        check(resolver.ResolveWithGeometry(visualPath, evidence with { Nodes = nodes.Select(node => node with { ControlType = "ControlType.Text" }).ToArray() }).Chat.Source == "Visual",
            "non-layout control types cannot be selected");
        var outside = evidence with { Nodes = [new UiaLayoutNode(1, 0, 1, "ControlType.Pane", new Rectangle(-1, 0, 1000, 800))] };
        check(resolver.ResolveWithGeometry(plain, outside).Chat.Source == "RatioFallback", "out-of-image UIA rectangles are rejected");
        check(visual.Diagnostics.Any(line => line.Contains("chat header separator")), "diagnostics retain the visual boundary evidence");

        var movedPath = Fixture("separators_moved", true, header: 167, editor: 541);
        var expandedHeader = resolver.ResolveWithGeometry(Fixture("expanded_summary_header", true, header: 211, editor: 550), unavailable);
        check(expandedHeader.Chat.Source == "Visual" && expandedHeader.Chat.Bounds.Top == 212 && expandedHeader.Chat.Bounds.Bottom is >= 550 and <= 551,
            "expanded summary header retains visual editor boundary rather than clipping newest bubble with ratio fallback");
        var moved = resolver.ResolveWithGeometry(movedPath, unavailable);
        check(moved.Chat.Bounds.Size == visual.Chat.Bounds.Size && moved.Chat.Bounds.Location != visual.Chat.Bounds.Location,
            "same-size visual fixture moves the chat rectangle without changing crop dimensions");
        var frameIndex = 0;
        var calls = 0;
        string? scratchPath = null;
        var monitor = new ChatRegionMonitor(scratch =>
        {
            scratchPath = scratch;
            var path = Path.Combine(scratch, "replay.png");
            File.Copy(frameIndex++ == 0 ? visualPath : movedPath, path, true);
            return Task.FromResult(new CaptureResult(true, path, "Test geometry replay", ""));
        });
        await monitor.RunAsync(3, _ => calls++, CancellationToken.None);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(scratchPath!)!, "summary.json")));
        check(calls == 0 && summary.RootElement.GetProperty("BaselineResets").GetInt32() == 1,
            "monitor resets baseline on changed layout bounds even with identical window/crop dimensions");
    }
}
