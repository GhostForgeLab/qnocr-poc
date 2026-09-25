using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ShadowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var start = new DateTime(2026, 9, 25, 12, 0, 0);
        BuyerMessage Message(string text, int seconds = 0, string product = "") => new() { Text = text, Timestamp = start.AddSeconds(seconds), ProductId = product };
        var old = Message("历史", -30);
        var tracker = new NewMessageTracker(start);
        check(tracker.Compare([old], start).NewMessages.Count == 0, "baseline suppresses historical envelopes");
        var five = new[] { "多少钱", "有现货吗", "可以刻字吗", "什么时候发货", "有没有大容量" }.Select((s, i) => Message(s, i + 1)).ToArray();
        var first = tracker.Compare([old, .. five], start.AddSeconds(10));
        check(first.NewMessages.Count == 5 && first.Decisions.Count(d => d.Disposition == "New") == 5, "five arrivals yield exactly five new messages");
        check(tracker.Compare(five, start.AddSeconds(11)).NewMessages.Count == 0, "identical OCR replay emits no new messages");
        check(tracker.Compare(five.Reverse().ToArray(), start.AddSeconds(12)).NewMessages.Count == 0, "reordering does not replay seen messages");
        var contextChanged = tracker.Compare([Message(five[0].Text, 1, "1051087533910")], start.AddSeconds(12));
        check(contextChanged.NewMessages.Count == 0 && contextChanged.Decisions[0].Disposition == "ContextChangedOnly", "product-only context change is not an arrival");
        check(MessageFingerprint.Create(five[0]) != MessageFingerprint.Create(Message(five[0].Text, 1, "1051087533910")), "fingerprint includes ProductId");
        check(MessageFingerprint.Create(five[0]) != MessageFingerprint.Create(Message(five[0].Text, 2)), "fingerprint includes timestamp");
        check(MessageFingerprint.Create(five[0]) != MessageFingerprint.Create(new BuyerMessage { Role = "seller", Text = five[0].Text, Timestamp = five[0].Timestamp }), "fingerprint includes role");
        check(MessageFingerprint.Create(five[0]) == MessageFingerprint.Create(Message(five[0].Text, 1)), "fingerprint is deterministic");
        check(tracker.Compare([Message("新滚出的旧历史", -20)], start.AddSeconds(12)).Decisions[0].Disposition == "HistoricalBeforeObservation", "unseen old history is not an arrival");
        check(tracker.Compare([Message("多少钱", 13)], start.AddSeconds(14)).NewMessages.Count == 1, "same text at a different new timestamp is distinct");
        check(tracker.Compare([Message("未来误读", 100)], start).NewMessages.Count == 0, "future OCR timestamp is review-only");
        var sameFrame = Message("same-frame", 14);
        check(tracker.Compare([sameFrame, sameFrame], start.AddSeconds(15)).NewMessages.Count == 1, "duplicates inside one batch suppressed");
        OcrResult ProductEvidence(string text) => new("synthetic", "fixture", [new(1, text, .99f, new(0, 0, 200, 30))], 0, .99, 0, 0, 0, 0, 0, 0);
        var extractor = new QnocrPoc.Product.ProductIdExtractor();
        check(extractor.Extract(ProductEvidence("ID 1051087533910□")).ProductId == "1051087533910", "copy-icon OCR square is a product ID delimiter");
        check(extractor.Extract(ProductEvidence("ID 1051087533910X")).ProductId is null, "alphabetic suffix is still rejected, no truncated product ID");

        var fixture = Path.GetFullPath("Output/ValidationV08/buyer_message.png");
        var mapping = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var count = 0;
        var noProduct = false;
        static ResolvedLayout Layout(int width = 800) => new(new(1000, 600),
            new(new(0, 500, 200, 100), "Fixture", "Synthetic"), new(new(0, 0, width, 500), "Fixture", "Synthetic"),
            new(new(800, 0, 200, 500), "Fixture", "Synthetic"), new(new(200, 500, 800, 100), "Fixture", "Synthetic"),
            new(false, "Not invoked", Rectangle.Empty, []), [], 0);
        OcrResult Ocr(string path)
        {
            var time = count <= 1 ? "2026-9-24 19:51:45" : "2026-9-25 12:00:00";
            OcrLine[] lines = Path.GetFileName(path) == "product_region.png"
                ? noProduct && count > 1 ? [] : [new(1, "商品ID：1051087533910", .99f, new(5, 10, 190, 25))]
                : [new(1, "buyer01 " + time, .99f, new(100, 80, 360, 25)),
                   new(2, "这个200ml还有货吗", .99f, new(112, 132, 280, 30)), new(3, "型号TA1", .99f, new(112, 174, 200, 30))];
            return new(path, "Synthetic OCR evidence; not Paddle accuracy test", lines, 0, .99, 0, 0, 0, 0, 0, 0);
        }
        Task<CaptureResult> Capture(string directory)
        {
            count++;
            var path = Path.Combine(directory, "fixture.png");
            using var canvas = new Bitmap(1000, 600);
            using (var g = Graphics.FromImage(canvas))
            using (var chat = new Bitmap(fixture))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(chat, 0, 0);
                // Frame3 changes pixels but carries identical OCR; frame4 is static.
                if (count >= 2) g.FillRectangle(Brushes.Black, 700, 400, count >= 3 ? 50 : 40, 40);
            }
            canvas.Save(path, ImageFormat.Png);
            return Task.FromResult(new CaptureResult(true, path, "Synthetic", "Owned test capture"));
        }
        QnocrShadowRunner Runner() => new(Capture, (_, _) => Task.FromResult(Layout()), Ocr, displayNow: () => start);
        var result = await Runner().RunAsync(new(4, 0, mapping));
        check(result.Status == "Completed" && result.SuccessfulFrames == 4, "bounded Shadow replay completes");
        check(result.ChatOcrCalls == 3 && result.ProductOcrCalls == 3, "each OCR pass reads current product, static frame skips OCR");
        check(result.NewMessages == 1 && result.BaselineMessages == 1 && result.DuplicateObservations == 1 && result.Envelopes == 1, "baseline plus repeated changed frames produce exactly one envelope");
        check(result.ConvertedMessages == 1 && result.ConvertedEnvelopes == 1, "new message traverses actual isolated adapter");
        check(!Directory.GetFiles(Path.Combine(result.SessionDirectory, "frame_000001"), "envelope_*.json").Any(), "baseline has no envelope file");
        var receipt = Directory.GetFiles(result.SessionDirectory, "shadow-adapter-result.json", SearchOption.AllDirectories).Single();
        using (var doc = JsonDocument.Parse(File.ReadAllText(receipt)))
        {
            var r = doc.RootElement;
            check(!r.GetProperty("CoordinatorCreated").GetBoolean() && r.GetProperty("ForbiddenBusinessCalls").GetInt32() == 0 && r.GetProperty("RuntimeInitializers").GetInt32() == 0 && r.GetProperty("SourceFilesVerified").GetInt32() == 6,
                "adapter verifies pinned sources and zero business/runtime initialization");
        }
        count = 0; noProduct = true;
        var empty = await Runner().RunAsync(new(2, 0, mapping));
        using (var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(empty.SessionDirectory, "envelope_*.json", SearchOption.AllDirectories).Single())))
            check(doc.RootElement.GetProperty("ProductId").GetString() == "", "unrecognized product never inherits baseline ID");
        count = 0;
        var unknown = await Runner().RunAsync(new(2, 0));
        check(unknown.DeferredEnvelopes == 1 && unknown.ConvertedMessages == 0 && unknown.Status == "CompletedWithIssues", "unknown identity is rejected by actual adapter");
        count = 0;
        Task<CaptureResult> Fail() { count++; return Task.FromResult(new CaptureResult(false, null, "Synthetic", "Simulated failure")); }
        var recovery = await new QnocrShadowRunner(d => count == 2 ? Fail() : Capture(d), (_, _) => Task.FromResult(Layout()), Ocr, displayNow: () => start).RunAsync(new(4, 0, mapping));
        check(recovery.FailedFrames == 1 && recovery.NewMessages == 1 && recovery.Envelopes == 1 && recovery.DuplicateObservations == 1, "capture failure preserves message deduplication");
        count = 0;
        var resize = await new QnocrShadowRunner(Capture, (_, _) => Task.FromResult(Layout(count <= 2 ? 800 : 799)), Ocr, displayNow: () => start).RunAsync(new(3, 0, mapping));
        check(resize.NewMessages == 1 && resize.DuplicateObservations == 1, "layout reset preserves fingerprints");
        count = 0;
        using var cancel = new CancellationTokenSource();
        var host = new ShadowAdapterClient();
        var cancelled = await new QnocrShadowRunner(Capture, (_, _) => Task.FromResult(Layout()), Ocr,
            async (e, i, o, t) => { var r = await host.ConvertAsync(e, i, o, t); cancel.Cancel(); return r; }, () => start).RunAsync(new(null, 0, mapping), cancel.Token);
        check(cancelled.Status == "Cancelled" && cancelled.Attempts == 2 && File.Exists(Path.Combine(cancelled.SessionDirectory, "summary.json")), "continuous cancellation persists summary");
        try { await Runner().RunAsync(new(0)); check(false, "invalid frames"); } catch (ArgumentOutOfRangeException) { check(true, "invalid frame count rejected"); }
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel(); count = 0;
        try { await Runner().RunAsync(new(1), preCancelled.Token); check(false, "pre-cancel"); } catch (OperationCanceledException) { check(count == 0, "pre-cancel prevents capture"); }
        var timeout = await new QnocrShadowRunner(_ => Task.FromResult(new CaptureResult(false, null, "Synthetic", "Timeout", TimedOut: true)), displayNow: () => start).RunAsync(new(5, 0));
        check(timeout.Attempts == 1 && timeout.Status == "CaptureTimedOut", "native timeout stops loop");
    }
}
