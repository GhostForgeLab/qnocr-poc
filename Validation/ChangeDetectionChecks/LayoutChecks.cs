using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using QnocrPoc.Capture;
using QnocrPoc.Detection;
using QnocrPoc.Models;

internal static class LayoutChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV05"));
        Directory.CreateDirectory(root);
        var ratios = new QianniuLayoutRatio();
        var original = ratios.Resolve(new Size(1638, 1100));
        check(original.ChatRegion == new Rectangle(380, 181, 654, 537) &&
            original.BuyerListRegion == new Rectangle(80, 120, 300, 980) &&
            original.ProductRegion == new Rectangle(1034, 120, 604, 924) &&
            original.InputRegion == new Rectangle(380, 718, 654, 382), "ratio layout exactly reproduces all V0.2 reference rectangles");
        var smaller = ratios.Resolve(new Size(1638, 949));
        check(smaller.ChatRegion == new Rectangle(380, 156, 654, 463) &&
            smaller.BuyerListRegion == new Rectangle(80, 104, 300, 845) &&
            smaller.ProductRegion == new Rectangle(1034, 104, 604, 797) &&
            smaller.InputRegion == new Rectangle(380, 619, 654, 330), "949-high layout matches expected ratio projection");
        foreach (var size in new[] { new Size(819, 550), new Size(1920, 1080), new Size(2226, 1253), new Size(1639, 951) })
        {
            var resolved = ratios.Resolve(size);
            check(resolved.GetRegions().All(region => region.Bounds.Width > 0 && region.Bounds.Height > 0 && new Rectangle(Point.Empty, size).Contains(region.Bounds)) &&
                resolved.BuyerListRegion.Right == resolved.ChatRegion.Left && resolved.ChatRegion.Right == resolved.ProductRegion.Left &&
                resolved.ChatRegion.Bottom == resolved.InputRegion.Top && resolved.InputRegion.Bottom == size.Height,
                $"{size.Width}x{size.Height}: all regions in bounds; shared edges align");
        }

        void Reject(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { check(true, message); return; }
            check(false, message);
        }
        Reject(() => new QianniuLayoutRatio { ChatLeft = double.NaN }.Resolve(new Size(1638, 949)), "NaN layout ratio rejected");
        Reject(() => new QianniuLayoutRatio { ChatHeight = double.PositiveInfinity }.Resolve(new Size(1638, 949)), "infinite layout ratio rejected");
        Reject(() => new QianniuLayoutRatio { BuyerListLeft = -0.1 }.Resolve(new Size(1638, 949)), "negative layout ratio rejected");
        Reject(() => new QianniuLayoutRatio { InputWidth = 0 }.Resolve(new Size(1638, 949)), "zero region ratio rejected");
        Reject(() => new QianniuLayoutRatio { InputWidth = 0.9 }.Resolve(new Size(1638, 949)), "out-of-bounds ratio rejected");
        Reject(() => ratios.Resolve(Size.Empty), "zero screenshot size rejected");
        Reject(() => ratios.Resolve(new Size(1, 1)), "image too small for nonempty ratio regions rejected");

        string MakeFrame(string name, int width, int height)
        {
            var path = Path.Combine(root, name + ".png");
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                for (var y = 0; y < height; y += 19)
                for (var x = 0; x < width; x += 17)
                {
                    using var brush = new SolidBrush(Color.FromArgb(x % 256, y % 256, (x + y) % 256));
                    graphics.FillRectangle(brush, x, y, 17, 19);
                }
            }
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        var source = MakeFrame("source_949", 1638, 949);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var extractor = new QianniuRegionExtractor();
        var output = Path.Combine(root, "regions_949");
        var paths = extractor.Extract(source, output, ratios);
        using (var full = new Bitmap(source))
        foreach (var (fileName, bounds) in smaller.GetRegions())
        {
            using var crop = new Bitmap(paths.Single(path => Path.GetFileName(path) == fileName));
            var exact = crop.Size == bounds.Size;
            foreach (var x in new[] { 0, crop.Width / 2, crop.Width - 1 })
            foreach (var y in new[] { 0, crop.Height / 2, crop.Height - 1 })
                exact &= crop.GetPixel(x, y) == full.GetPixel(bounds.X + x, bounds.Y + y);
            check(exact, $"{fileName}: exact pixel crop, correct size, no source resampling");
        }
        check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "source PNG unchanged after ratio extraction");
        var invalidDirectory = Path.Combine(root, "invalid_" + Guid.NewGuid().ToString("N"));
        Reject(() => extractor.Extract(source, invalidDirectory, new QianniuLayoutRatio { InputHeight = 1 }), "invalid final region rejected before extraction");
        check(!Directory.Exists(invalidDirectory), "invalid ratios leave no partial output directory");

        // Run the actual monitor loop with recorded synthetic frames; never resize the user's window.
        var sequence = new[] { MakeFrame("source_1100", 1638, 1100), source,
            MakeFrame("source_1640", 1640, 949), MakeFrame("source_1639", 1639, 949) };
        check(ratios.Resolve(new Size(1640, 949)).ChatRegion.Size == ratios.Resolve(new Size(1639, 949)).ChatRegion.Size,
            "resize fixture includes different window sizes that produce equal chat crop sizes");
        var frameIndex = 0;
        var ocrCalls = 0;
        string? scratchPath = null;
        var monitor = new ChatRegionMonitor(scratch =>
        {
            scratchPath = scratch;
            var path = Path.Combine(scratch, "replay.png");
            File.Copy(sequence[Math.Min(frameIndex++, sequence.Length - 1)], path, true);
            return Task.FromResult(new CaptureResult(true, path, "Test replay", ""));
        });
        await monitor.RunAsync(5, _ => ocrCalls++, CancellationToken.None);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(scratchPath!)!, "summary.json")));
        check(frameIndex == 5 && ocrCalls == 0 && summary.RootElement.GetProperty("BaselineResets").GetInt32() == 3 &&
            summary.RootElement.GetProperty("Status").GetString() == "Completed", "monitor replay: 3 window resizes reset baseline, subsequent identical frame skips OCR");
    }
}
