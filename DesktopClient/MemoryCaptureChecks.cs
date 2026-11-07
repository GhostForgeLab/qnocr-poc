using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Capture;
using QnocrPoc.Detection;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class MemoryCaptureChecks
{
    public static async Task<int> Run(bool live = false)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var gate = new SettledFrameGate(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1500));
        TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);
        Check(!gate.Observe("a", At(0)) && !gate.Observe("a", At(299)) && gate.Observe("a", At(300)), "first frame waits for stability");
        gate.Complete("a");
        Check(Enumerable.Range(1, 10000).All(i => !gate.Observe("a", At(300 + i))), "10000 identical observations do not trigger OCR");
        Check(!gate.Observe("b", At(11000)) && !gate.Observe("c", At(11200)) && !gate.Observe("c", At(11499)) && gate.Observe("c", At(11500)), "scroll animation debounces to newest stable frame");
        gate.Complete("c");
        Check(!gate.Observe("transient", At(12000)) && !gate.Observe("c", At(12100)) && !gate.Observe("c", At(12500)), "return to processed frame skips redundant OCR");
        for (var t = 13000; t < 14500; t += 100) Check(!gate.Observe(t.ToString(), At(t)), "continuous animation waits within maximum");
        Check(gate.Observe("last", At(14500)), "continuous animation cannot starve recognition");
        gate.Complete("last"); gate.Reset();
        Check(!gate.Observe("last", At(15000)) && gate.Observe("last", At(15300)), "restore/reconnect revalidates unchanged content");
        Check(JsonSerializer.Deserialize<ClientSettings>("{\"PollSeconds\":60,\"SellerId\":\"shop\"}") is { CaptureIntervalMilliseconds: 500, SellerId: "shop" }, "old interval migrates without losing shop");
        using (var pixels = new Bitmap(240, 120, PixelFormat.Format32bppRgb))
        {
            using (var graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.Black);
            Check(QianniuWindowCapture.InspectPixels(pixels) is { Suspect: true, BlackRatio: 1 }, "buffer check rejects black captures");
            using (var graphics = Graphics.FromImage(pixels)) graphics.Clear(Color.Magenta);
            Check(QianniuWindowCapture.InspectPixels(pixels) is { Suspect: true, UnpaintedRatio: 1 }, "buffer check rejects unpainted captures");
            using (var graphics = Graphics.FromImage(pixels)) { graphics.Clear(Color.White); graphics.FillRectangle(Brushes.Black, 20, 20, 50, 50); }
            var before = QianniuWindowCapture.InspectPixels(pixels);
            Check(!before.Suspect && before.Fingerprint == QianniuWindowCapture.InspectPixels(pixels).Fingerprint, "same visible pixels have stable fingerprint");
            pixels.SetPixel(120, 50, Color.Red);
            Check(before.Fingerprint != QianniuWindowCapture.InspectPixels(pixels).Fingerprint, "one pixel change invalidates frame fingerprint");
        }

        byte[] Picture(Action<Graphics>? draw = null)
        {
            using var bmp = new Bitmap(1000, 700);
            using (var graphics = Graphics.FromImage(bmp)) { graphics.Clear(Color.FromArgb(240, 240, 240)); draw?.Invoke(graphics); }
            using var stream = new MemoryStream(); bmp.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
        var clean = Picture(); var frame = MemoryChatFrame.FromPng(clean); var layout = frame.Layout;
        byte[] Changed(Rectangle area) => Picture(g => g.FillRectangle(Brushes.Black, area.Left + 10, area.Top + 10, 12, 12));
        Check(MemoryChatFrame.FromPng(Changed(layout.InputRegion)).Fingerprint == frame.Fingerprint, "typing/caret excluded from recognition trigger");
        Check(MemoryChatFrame.FromPng(Changed(layout.BuyerListRegion)).Fingerprint == frame.Fingerprint, "other buyers' list updates excluded");
        Check(MemoryChatFrame.FromPng(Changed(layout.ChatRegion)).Fingerprint != frame.Fingerprint, "new chat pixels trigger recognition");
        Check(MemoryChatFrame.FromPng(Changed(layout.ProductRegion)).Fingerprint != frame.Fingerprint, "product changes invalidate context");
        Check(MemoryChatFrame.FromPng(Changed(new Rectangle(layout.ChatRegion.Left, layout.BuyerListRegion.Top, layout.ChatRegion.Width, layout.ChatRegion.Top - layout.BuyerListRegion.Top))).ChatFingerprint != frame.ChatFingerprint,
            "conversation header switches invalidate identical messages");
        Check(MemoryChatFrame.FromPng(Changed(layout.ProductRegion)).ChatFingerprint == frame.ChatFingerprint, "product-only animation does not trigger chat OCR");
        Check(MemoryChatFrame.FromPng(Changed(new Rectangle(layout.ChatRegion.Left, 0, layout.ChatRegion.Width, layout.BuyerListRegion.Top))).ChatFingerprint == frame.ChatFingerprint,
            "top toolbar updates do not trigger chat OCR");
        var emptyOcr = new OcrResult("memory:synthetic", "controlled", Array.Empty<OcrLine>(), 0, null, 0, 0, 0, 0, 0, 0);
        Check(new BuyerMessageStructurer().Structure(emptyOcr, frame.Chat).Messages.Count == 0, "message structuring accepts memory sources without inventing messages");

        // Exercise the actual native process and OCR model twice, entirely through the pipe.
        var text = Picture(g => { using var font = new Font("Arial", 30); g.DrawString("HELLO 12345", font, Brushes.Black, 100, 150); });
        var filesBefore = Directory.GetFiles("Output", "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var worker = await MemoryNativeWorker.StartAsync(CancellationToken.None);
        using var process = Process.GetProcessById(worker.ProcessId);
        try
        {
            var first = await worker.RecognizeAsync(text, "memory:test1", CancellationToken.None);
            var second = await worker.RecognizeAsync(text, "memory:test2", CancellationToken.None);
            Check(first.Text.Contains("12345") && second.Text.Contains("12345"), "real native OCR recognizes in-memory pixels twice");
            Check(second.InitializationMilliseconds < first.InitializationMilliseconds, "persistent worker reuses initialized model");
            process.Refresh();
            Check(process.PriorityClass == ProcessPriorityClass.BelowNormal, "OCR process yields priority to foreground typing");
            Console.WriteLine($"Worker memory={process.WorkingSet64 / 1024 / 1024}MB threads={process.Threads.Count}");
            Console.WriteLine($"OCR cold={first.TotalMilliseconds:F0}ms warm={second.TotalMilliseconds:F0}ms");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await worker.RecognizeAsync(text, "memory:cancelled", cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "cancellation aborts pending IPC"); }
        }
        finally { await worker.DisposeAsync(); }
        Check(process.HasExited, "disconnect/cancellation reaps owned native process");
        Check(!Directory.GetFiles("Output", "*", SearchOption.AllDirectories).Except(filesBefore).Any(), "capture/layout/OCR checks created no disk images or result files");

        if (live)
        {
            var window = QianniuConnection.Windows().FirstOrDefault(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked)
                ?? throw new InvalidOperationException("没有可用的千牛接待窗口。");
            await using var capture = await MemoryNativeWorker.StartAsync(CancellationToken.None);
            var timer = Stopwatch.StartNew();
            var sample = await capture.CaptureAsync(window, CancellationToken.None);
            Check(sample.Png is { Length: > 0 }, "live PrintWindow frame received entirely in memory");
            var sampleFrame = MemoryChatFrame.FromPng(sample.Png!);
            Console.WriteLine($"Live capture+layout={timer.ElapsedMilliseconds}ms, compressed frame={sample.Png!.Length} bytes");
            var result = await capture.RecognizeAsync(sampleFrame.Chat, "memory:live", CancellationToken.None);
            var batch = new BuyerMessageStructurer().Structure(result, sampleFrame.Chat);
            Console.WriteLine($"Live OCR lines={result.Lines.Count}, confirmed buyer messages={batch.Messages.Count}; no text or images saved.");
            using var captureProcess = Process.GetProcessById(capture.ProcessId);
            captureProcess.Refresh(); var cpuBefore = captureProcess.TotalProcessorTime;
            var idleTimer = Stopwatch.StartNew(); int unchanged = 0;
            for (var i = 0; i < 12; i++)
            {
                var next = await capture.CaptureAsync(window, CancellationToken.None);
                Check(next.Waiting || next.Unchanged && next.Png is null || !next.Unchanged && next.Png is { Length: > 0 },
                    "live unchanged-frame protocol remains consistent");
                if (next.Unchanged) unchanged++;
                await Task.Delay(500);
            }
            captureProcess.Refresh();
            Console.WriteLine($"Live sample: unchanged={unchanged}/12, worker average CPU cores={(captureProcess.TotalProcessorTime - cpuBefore).TotalMilliseconds / idleTimer.Elapsed.TotalMilliseconds:F3}, memory={captureProcess.WorkingSet64 / 1024 / 1024}MB");
            Check(!Directory.GetFiles("Output", "*", SearchOption.AllDirectories).Except(filesBefore).Any(), "live capture and OCR created no disk files");
        }
        Console.WriteLine($"Passed {count} memory capture checks.");
        return 0;
    }
}
