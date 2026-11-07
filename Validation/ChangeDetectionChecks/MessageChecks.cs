using System.Drawing;
using System.Drawing.Imaging;
using QnocrPoc.Messages;
using QnocrPoc.Models;

internal static class MessageChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV08"));
        Directory.CreateDirectory(root);
        static OcrLine Line(int n, string text, OcrBounds bounds, float? confidence = .99f) => new(n, text, confidence, bounds);
        static OcrResult Ocr(string image, params OcrLine[] lines) => new(image, "Synthetic OCR evidence", lines, 0, null, 0, 0, 0, 0, 0, 0);
        var header = Line(1, "buyer01 2026-9-24 19:51:45", new(100, 80, 360, 25));
        var first = Line(2, "这个200ml还有货吗", new(112, 132, 280, 30));
        var second = Line(3, "型号TA1", new(112, 174, 200, 30));

        string Fixture(string name, Color? bubbleColor = null, bool right = false, bool blackHeader = false, bool splitHeader = false, bool twoBubbles = false)
        {
            var path = Path.Combine(root, name + ".png");
            using var bitmap = new Bitmap(800, 500);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var bodyFont = new Font("Microsoft YaHei", 22, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var captionFont = new Font("Microsoft YaHei", 18, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var gray = new SolidBrush(blackHeader ? Color.Black : Color.FromArgb(153, 153, 153)))
            using (var white = new SolidBrush(bubbleColor ?? Color.White))
            {
                graphics.Clear(Color.FromArgb(240, 242, 245));
                var x = right ? 450 : 100;
                graphics.FillRectangle(white, x, 120, right ? 300 : 400, 120);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                if (splitHeader)
                {
                    graphics.DrawString("buyer01", captionFont, gray, 100, 80);
                    graphics.DrawString("2026-9-24 19:51:45", captionFont, gray, 185, 80);
                }
                else graphics.DrawString(header.Text, captionFont, gray, x, 80);
                graphics.DrawString(first.Text, bodyFont, Brushes.Black, x + 12, 132);
                graphics.DrawString(second.Text, bodyFont, Brushes.Black, x + 12, 174);
                if (twoBubbles)
                {
                    graphics.DrawString("buyer01 2026-9-24 19:52:00", captionFont, gray, 100, 300);
                    graphics.FillRectangle(white, 100, 340, 400, 80);
                    graphics.DrawString(first.Text, bodyFont, Brushes.Black, 112, 352);
                }
            }
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }

        var source = Fixture("buyer_message");
        var parser = new BuyerMessageStructurer();
        var result = parser.Structure(Ocr(source, header, first, second));
        check(result.Messages.Count == 1 && result.Messages[0].Role == "buyer" && result.Messages[0].Text == first.Text + "\n" + second.Text,
            "multiline OCR is grouped inside one buyer bubble without header text");
        check(result.Messages[0].Timestamp == new DateTime(2026, 9, 24, 19, 51, 45) && result.Messages[0].Timestamp.Kind == DateTimeKind.Unspecified,
            "timestamp comes from screenshot header; no current-date or timezone substitution");
        var joined = parser.Structure(Ocr(source, header with { Text = "buyer01 2026-9-2419:51:45" }, first, second));
        check(joined.Messages.Count == 1 && joined.Messages[0].Timestamp == result.Messages[0].Timestamp,
            "full timestamp remains valid when OCR removes the date-time space");
        check(parser.Structure(Ocr(source, header with { Text = "buyer01 ２０２６－９－２４１９：５１：４５" }, first, second)).Messages.Count == 1,
            "joined timestamps also retain existing full-width normalization");
        foreach (var joinedInvalid in new[] { "buyer01 2026-9-2419:51", "buyer01 2026-9-2429:51:45", "buyer01 2026-9-419:51:45", "buyer01 2026-2-3019:51:45" })
            check(parser.Structure(Ocr(source, header with { Text = joinedInvalid }, first, second)).Messages.Count == 0,
                "joined timestamps still reject partial, invalid or ambiguous components: " + joinedInvalid);
        check(result.Messages[0].ProductId == "" && result.ProductBinding == "None" && result.Messages[0].Source == "Qianniu.PaddleOCR",
            "unknown product remains empty and source is explicit");
        check(result.Evidence.Count == 1 && result.Evidence[0].BodyLineNumbers.SequenceEqual(new[] { 2, 3 }) && result.ImageSha256.Length == 64,
            "message evidence records source lines, bubble and image hash");
        check(parser.Structure(Ocr(source, header, first), "1051087533910", "TestExplicitContext").Messages[0].ProductId == "1051087533910",
            "explicit product context is copied without catalog guessing");

        foreach (var changedHeader in new[] { header with { Text = "buyer01 19:51:45" }, header with { Text = "buyer01 2026-13-24 19:51:45" }, header with { Confidence = .3f } })
        {
            var pending = parser.Structure(Ocr(source, changedHeader, first));
            check(pending.Messages.Count == 0 && pending.ReviewItems.Any(item => item.Reason == "MissingReliableFullTimestampAndBuyerHeader"),
                "missing/invalid/low-confidence timestamp is review-only: " + changedHeader.Text);
        }
        var noHeader = parser.Structure(Ocr(source, first));
        check(noHeader.Messages.Count == 0 && noHeader.ReviewItems.Any(item => item.Text.Contains(first.Text)), "missing header never borrows capture time");
        check(parser.Structure(Ocr(source, first, header with { Number = 50, Text = "未知灰色文字" })).ReviewItems.Any(item => item.LineNumbers.Contains(50)),
            "unassigned gray OCR is kept for review rather than silently discarded");
        var clippedPath = Path.Combine(root, "clipped.png");
        using (var full = new Bitmap(source))
        using (var clipped = full.Clone(new Rectangle(0, 130, 800, 370), full.PixelFormat)) clipped.Save(clippedPath, ImageFormat.Png);
        var clippedResult = parser.Structure(Ocr(clippedPath, first with { Bounds = new(112, 2, 280, 30) }));
        check(clippedResult.Messages.Count == 0 && clippedResult.ReviewItems.Any(item => item.Reason == "ClippedBubble"), "clipped buyer bubble is review-only");
        var lowBody = parser.Structure(Ocr(source, header, first, second with { Confidence = .2f }));
        check(lowBody.Messages.Count == 0 && lowBody.ReviewItems.Any(item => item.Reason == "LowConfidenceBody" && item.LineNumbers.Count == 2),
            "one low-confidence body line holds the whole bubble for review instead of silently dropping text");
        check(parser.Structure(Ocr(source, header, first with { Confidence = float.NaN })).Messages.Count == 0, "nonfinite body confidence cannot produce a message");
        var conflicts = parser.Structure(Ocr(source, header, header with { Number = 4, Text = "buyer01 2026-9-24 19:52:00" }, first));
        check(conflicts.Messages.Count == 0, "conflicting nearest header timestamps remain unresolved");
        var timestampBody = parser.Structure(Ocr(source, header, first with { Text = "2026-9-25 09:00:00" }));
        check(timestampBody.Messages.Count == 1 && timestampBody.Messages[0].Text == "2026-9-25 09:00:00" && timestampBody.Messages[0].Timestamp == headerTime(),
            "a date inside the message body is kept as text, not used as the message timestamp");
        static DateTime headerTime() => new(2026, 9, 24, 19, 51, 45);

        var split = Fixture("split_header", splitHeader: true);
        var splitResult = parser.Structure(Ocr(split, Line(10, "buyer01", new(100, 80, 75, 25)), Line(11, "2026-9-24 19:51:45", new(185, 80, 220, 25)), first));
        check(splitResult.Messages.Count == 1 && splitResult.Evidence[0].HeaderLineNumbers.Count == 2, "split nickname/time header uses geometry, not OCR array order");
        var overlap = parser.Structure(Ocr(split, Line(10, "buyer01", new(100, 80, 94, 25)), Line(11, "2026-9-24 19:51:45", new(185, 80, 220, 25)), first));
        check(overlap.Messages.Count == 1, "nine-pixel OCR header box overlap is accepted with otherwise reliable unique gray header");
        var excessiveOverlap = parser.Structure(Ocr(split, Line(10, "buyer01", new(100, 80, 115, 25)), Line(11, "2026-9-24 19:51:45", new(185, 80, 220, 25)), first));
        check(excessiveOverlap.Messages.Count == 0, "header boxes overlapping more than half a line height remain unconfirmed");
        var blue = Fixture("seller_blue", Color.FromArgb(232, 239, 255));
        check(parser.Structure(Ocr(blue, header, first)).Messages.Count == 0, "blue seller-style bubble is not classified as buyer");
        var right = Fixture("right_white", right: true);
        var rightResult = parser.Structure(Ocr(right, header with { Bounds = new(450, 80, 340, 25) }, first with { Bounds = new(462, 132, 280, 30) }));
        check(rightResult.Messages.Count == 0 && rightResult.ReviewItems.Count > 0, "right-side white content remains role-unconfirmed");
        var black = Fixture("black_caption", blackHeader: true);
        check(parser.Structure(Ocr(black, header, first)).Messages.Count == 0, "black body-like text above a bubble is not trusted as a gray timestamp header");
        var two = Fixture("two_bubbles", twoBubbles: true);
        var repeated = parser.Structure(Ocr(two, header, first, Line(4, "buyer01 2026-9-24 19:52:00", new(100, 300, 360, 25)), Line(5, first.Text, new(112, 352, 280, 30))));
        check(repeated.Messages.Count == 2 && repeated.Messages[0].Timestamp != repeated.Messages[1].Timestamp,
            "identical text in separate timestamped bubbles stays two messages (no cross-message deduplication)");
        foreach (var punctuation in new[] { "？？？", "……", "！", "👍", "😊❤️", "[微笑]", "价格？😊" })
            check(parser.Structure(Ocr(source, header, first with { Text = punctuation })).Messages.Single().Text == punctuation,
                "trusted bubble preserves punctuation/symbols: " + punctuation);
        check(parser.Structure(Ocr(source, header, first with { Text = "\u200B" })).Messages.Count == 0, "control-only body remains unaccepted");
        var visualParser = new BuyerMessageStructurer(true);
        var visual = visualParser.Structure(Ocr(source, header)).Messages.Single();
        check(visual.ContentKind == "Unrecognized" && visual.PreviewPng is { Length: > 0 } && visual.Text.Contains("未识别"),
            "unreadable body retains image preview with explicit uncertainty and verified header");
        check(visualParser.Structure(Ocr(source)).Messages.Count == 0, "visual-only region still requires buyer and full timestamp proof");
        check(!System.Text.Json.JsonSerializer.Serialize(visual).Contains("PreviewPng"), "preview bytes are not persisted in message JSON");
        using (var sticker = new Bitmap(800, 500))
        using (var graphics = Graphics.FromImage(sticker))
        using (var font = new Font("Microsoft YaHei", 18, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var grayBrush = new SolidBrush(Color.FromArgb(153, 153, 153)))
        {
            graphics.Clear(Color.FromArgb(240, 242, 245));
            graphics.DrawString(header.Text, font, grayBrush, 100, 80);
            graphics.FillEllipse(Brushes.OrangeRed, 100, 120, 65, 65);
            using var png = new MemoryStream(); sticker.Save(png, ImageFormat.Png);
            var standalone = visualParser.Structure(Ocr("memory:sticker", header), png.ToArray());
            check(standalone.Messages.Count == 1 && standalone.Messages[0].ContentKind == "Unrecognized", "standalone colored sticker with reliable buyer/time is visible as a preview");
            check(visualParser.Structure(Ocr("memory:sticker"), png.ToArray()).Messages.Count == 0, "standalone colored region without header is not attributed to a buyer");
        }
        var changedVisual = Fixture("visual-next-frame");
        using (var pixels = new Bitmap(changedVisual))
        {
            pixels.SetPixel(150, 150, Color.Red);
            var bytes = new MemoryStream(); pixels.Save(bytes, ImageFormat.Png);
            var animation = visualParser.Structure(Ocr(changedVisual, header), bytes.ToArray()).Messages.Single();
            check(MessageFingerprint.ConversationMessageKey(animation) == MessageFingerprint.ConversationMessageKey(visual),
                "animated pixels do not generate repeated arrivals for one timestamped visual message");
        }
        try { parser.Structure(Ocr(source, header, first), "not-an-id"); check(false, "invalid product ID rejected"); }
        catch (ArgumentException) { check(true, "invalid product ID rejected"); }
    }
}
