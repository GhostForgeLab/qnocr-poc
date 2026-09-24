using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Capture;
using QnocrPoc.Models;

namespace QnocrPoc.Detection;

public sealed class ChatRegionMonitor
{
    public async Task RunAsync(int? maximumFrames, Action<string> runOcr, CancellationToken cancellationToken)
    {
        var capture = new QianniuWindowCapture();
        var window = capture.FindWindows(Console.WriteLine).FirstOrDefault(w =>
            w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No visible reception center found. Restore the Qianniu chat window and retry.");
        var layout = new QianniuLayout();
        var pipeline = new ChatRegionChangePipeline();
        var session = Path.GetFullPath(Path.Combine("Output", "Changes", $"session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"));
        var scratch = Path.Combine(session, "frames");
        Directory.CreateDirectory(scratch);
        Console.WriteLine($"Watching HWND: 0x{window.Hwnd:X}\nSession: {session}\nInterval: 1000 ms after processing; first frame is baseline only. Ctrl+C stops after the current operation.");
        string? previousPath = null;
        var frames = 0;
        var changes = 0;
        var ocrCalls = 0;
        var status = "Completed";
        try
        {
            while (maximumFrames is null || frames < maximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captureResult = await capture.CaptureAsync(window, scratch);
                if (!captureResult.Success || captureResult.SavedPath is null)
                    throw new InvalidOperationException(captureResult.Message);
                var currentPath = Path.Combine(scratch, $"chat_{frames % 2}.png");
                try
                {
                    using var full = new Bitmap(captureResult.SavedPath);
                    if (full.Size != layout.ScreenshotSize)
                        throw new InvalidOperationException($"Watch layout requires 1638x1100; captured {full.Width}x{full.Height}. Recalibration required.");
                    using var chat = full.Clone(layout.ChatRegion, full.PixelFormat);
                    chat.Save(currentPath, ImageFormat.Png);
                }
                finally { File.Delete(captureResult.SavedPath); }
                frames++;
                Console.WriteLine($"\nFrame {frames}: {DateTimeOffset.Now:O}");
                if (previousPath is null) Console.WriteLine("Baseline established. OCR skipped.");
                else
                {
                    pipeline.Process(previousPath, currentPath, session, path =>
                    {
                        changes++;
                        cancellationToken.ThrowIfCancellationRequested();
                        ocrCalls++;
                        runOcr(path);
                    }, Console.WriteLine);
                }
                previousPath = currentPath;
                if (maximumFrames is null || frames < maximumFrames)
                    await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = "Cancelled";
        }
        catch
        {
            status = "Failed";
            throw;
        }
        finally
        {
            var summary = new { Status = status, FramesCaptured = frames, ChangedFrames = changes, OcrCalls = ocrCalls };
            File.WriteAllText(Path.Combine(session, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Watch stopped: {status}; Frames={frames}; ChangedFrames={changes}; OCR calls={ocrCalls}");
        }
    }
}
