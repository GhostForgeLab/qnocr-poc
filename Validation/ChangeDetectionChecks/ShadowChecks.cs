using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class ShadowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var fixture = Path.GetFullPath("Output/ValidationV08/buyer_message.png"); // Created by MessageChecks.
        var mapping = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var host = new ShadowAdapterClient();
        static ResolvedLayout Layout(int width = 800) => new(new(1000, 600),
            new(new(0, 500, 200, 100), "Fixture", "Synthetic"), new(new(0, 0, width, 500), "Fixture", "Synthetic"),
            new(new(800, 0, 200, 500), "Fixture", "Synthetic"), new(new(200, 500, 800, 100), "Fixture", "Synthetic"),
            new(false, "Not invoked", Rectangle.Empty, []), [], 0);
        static OcrResult Ocr(string path)
        {
            OcrLine[] lines = Path.GetFileName(path) == "product_region.png"
                ? [new(1, "商品ID：1051087533910", .99f, new(5, 10, 190, 25))]
                : [new(1, "buyer01 2026-9-24 19:51:45", .99f, new(100, 80, 360, 25)),
                   new(2, "这个200ml还有货吗", .99f, new(112, 132, 280, 30)),
                   new(3, "型号TA1", .99f, new(112, 174, 200, 30))];
            return new(path, "Synthetic OCR evidence; not Paddle accuracy test", lines, 0, .99, 0, 0, 0, 0, 0, 0);
        }
        Task<CaptureResult> Capture(string directory, bool changed)
        {
            var path = Path.Combine(directory, "fixture.png");
            using var canvas = new Bitmap(1000, 600);
            using (var g = Graphics.FromImage(canvas))
            using (var chat = new Bitmap(fixture))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(chat, 0, 0);
                if (changed) g.FillRectangle(Brushes.Black, 700, 400, 40, 40);
            }
            canvas.Save(path, ImageFormat.Png);
            return Task.FromResult(new CaptureResult(true, path, "Synthetic", "Owned test capture"));
        }
        static JsonElement[] Log(ShadowRunSummary summary) => File.ReadAllLines(Path.Combine(summary.SessionDirectory, "shadow.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var captureCount = 0;
        var runner = new QnocrShadowRunner(d => Capture(d, ++captureCount >= 3), (_, _) => Task.FromResult(Layout()), Ocr);
        var result = await runner.RunAsync(new(4, 0, mapping));
        check(result.Status == "Completed" && result.SuccessfulFrames == 4 && result.FailedFrames == 0, "Shadow bounded replay completes four frames");
        check(result.ChatOcrCalls == 2 && result.ProductOcrCalls == 1, "Shadow A-A-B-B runs chat OCR twice and caches identical product pixels");
        check(result.Envelopes == 2 && result.ConvertedEnvelopes == 2, "Shadow confirmed OCR batches traverse actual isolated qnbot adapter");
        var logs = Log(result);
        check(logs.Select(l => l.GetProperty("Kind").GetString()).SequenceEqual(new[] { "BaselineSnapshot", "Unchanged", "ChangedSnapshot", "Unchanged" }), "Shadow marks baseline, changes and unchanged frames explicitly");
        check(logs.All(l => !l.GetProperty("IsNewMessage").GetBoolean() && l.GetProperty("IsTestIdentity").GetBoolean()), "Shadow snapshots do not claim newness or real identity");
        var receipts = Directory.GetFiles(result.SessionDirectory, "shadow-adapter-result.json", SearchOption.AllDirectories);
        check(receipts.Length == 2 && receipts.All(p =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            return !r.GetProperty("CoordinatorCreated").GetBoolean() && r.GetProperty("ForbiddenBusinessCalls").GetInt32() == 0
                && r.GetProperty("RuntimeInitializers").GetInt32() == 0 && r.GetProperty("SourceFilesVerified").GetInt32() == 6;
        }), "Shadow conversion receipts verify six pinned sources and zero coordinator/business/runtime initialization");

        var unknown = await new QnocrShadowRunner(d => Capture(d, false), (_, _) => Task.FromResult(Layout()), Ocr).RunAsync(new(1, 0));
        check(unknown.Status == "CompletedWithIssues" && unknown.DeferredEnvelopes == 1 && unknown.ConvertedEnvelopes == 0, "Shadow missing identity is deferred by actual adapter");
        using (var doc = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(unknown.SessionDirectory, "envelope_*.json", SearchOption.AllDirectories).Single())))
            check(doc.RootElement.GetProperty("SellerId").GetString() == "" && doc.RootElement.GetProperty("BuyerId").GetString() == "", "Shadow does not invent unknown identities");

        captureCount = 0;
        var recovery = await new QnocrShadowRunner(d => ++captureCount == 2
            ? Task.FromResult(new CaptureResult(false, null, "Synthetic", "Simulated capture failure")) : Capture(d, false),
            (_, _) => Task.FromResult(Layout()), Ocr).RunAsync(new(3, 0, mapping));
        check(recovery.Attempts == 3 && recovery.FailedFrames == 1 && recovery.SuccessfulFrames == 2 && recovery.Status == "CompletedWithIssues", "Shadow capture failure consumes an attempt and bounded loop recovers");
        check(recovery.ChatOcrCalls == 2 && recovery.ProductOcrCalls == 2 && Log(recovery)[2].GetProperty("Kind").GetString() == "BaselineSnapshot", "Shadow failure clears baseline and product cache instead of reusing stale context");

        var layoutCount = 0;
        var resize = await new QnocrShadowRunner(d => Capture(d, false), (_, _) => Task.FromResult(Layout(++layoutCount == 1 ? 800 : 799)), Ocr)
            .RunAsync(new(2, 0, mapping));
        check(resize.FailedFrames == 0 && resize.ChatOcrCalls == 2 && Log(resize)[1].GetProperty("Kind").GetString() == "LayoutResetSnapshot", "Shadow changed chat bounds reset baseline without mismatched-size comparison");

        using var cancel = new CancellationTokenSource();
        var cancelled = await new QnocrShadowRunner(d => Capture(d, false), (_, _) => Task.FromResult(Layout()), Ocr,
            async (e, i, o, t) => { var r = await host.ConvertAsync(e, i, o, t); cancel.Cancel(); return r; })
            .RunAsync(new(null, 1000, mapping), cancel.Token);
        check(cancelled.Status == "Cancelled" && cancelled.Attempts == 1 && File.Exists(Path.Combine(cancelled.SessionDirectory, "summary.json")), "Shadow cancellation stops continuous loop and persists summary");
        try { await runner.RunAsync(new(0)); check(false, "Shadow rejects invalid frame limits"); }
        catch (ArgumentOutOfRangeException) { check(true, "Shadow rejects invalid frame limits"); }
        captureCount = 0;
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        try { await runner.RunAsync(new(1), preCancelled.Token); check(false, "Shadow respects pre-cancellation"); }
        catch (OperationCanceledException) { check(captureCount == 0, "Shadow pre-cancellation prevents capture and startup"); }
        var timedOut = await new QnocrShadowRunner(_ => Task.FromResult(new CaptureResult(false, null, "Synthetic", "Simulated native timeout", TimedOut: true)),
            (_, _) => Task.FromResult(Layout()), Ocr).RunAsync(new(5, 0));
        check(timedOut.Status == "CaptureTimedOut" && timedOut.Attempts == 1 && timedOut.ChatOcrCalls == 0, "Shadow stops after native capture timeout instead of accumulating blocked workers");
    }
}
