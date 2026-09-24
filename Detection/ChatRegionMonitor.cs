using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Capture;
using QnocrPoc.Models;
using QnocrPoc.Layout;

namespace QnocrPoc.Detection;

public sealed class ChatRegionMonitor
{
    private readonly Func<string, Task<CaptureResult>>? _captureFrame;

    // An optional frame source enables deterministic resize/cancellation validation without UI actions.
    public ChatRegionMonitor(Func<string, Task<CaptureResult>>? captureFrame = null) => _captureFrame = captureFrame;

    public async Task RunAsync(int? maximumFrames, Action<string> runOcr, CancellationToken cancellationToken)
    {
        var captureFrame = _captureFrame;
        var sourceDescription = "Supplied frame source";
        QianniuWindow? window = null;
        if (captureFrame is null)
        {
            var capture = new QianniuWindowCapture();
            window = capture.FindWindows(Console.WriteLine).FirstOrDefault(w =>
                w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("No visible reception center found. Restore the Qianniu chat window and retry.");
            sourceDescription = $"Watching HWND: 0x{window.Hwnd:X}";
            captureFrame = directory => capture.CaptureAsync(window, directory);
        }
        var resolver = new QianniuLayoutResolver();
        var pipeline = new ChatRegionChangePipeline();
        var session = Path.GetFullPath(Path.Combine("Output", "Changes", $"session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"));
        var scratch = Path.Combine(session, "frames");
        Directory.CreateDirectory(scratch);
        Console.WriteLine($"{sourceDescription}\nSession: {session}\nInterval: 1000 ms after processing; first frame is baseline only. Ctrl+C stops after the current operation.");
        string? previousPath = null;
        Size? previousFrameSize = null;
        Rectangle? previousChatBounds = null;
        var frames = 0;
        var changes = 0;
        var ocrCalls = 0;
        var baselineResets = 0;
        var status = "Completed";
        try
        {
            while (maximumFrames is null || frames < maximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var captureResult = await captureFrame(scratch);
                if (!captureResult.Success || captureResult.SavedPath is null)
                    throw new InvalidOperationException(captureResult.Message);
                var currentPath = Path.Combine(scratch, $"chat_{frames % 2}.png");
                Size currentFrameSize;
                Rectangle chatBounds;
                try
                {
                    var resolved = await resolver.ResolveAsync(captureResult.SavedPath, window);
                    QianniuLayoutResolver.SaveDiagnostics(resolved, captureResult.SavedPath, Console.WriteLine);
                    using var full = new Bitmap(captureResult.SavedPath);
                    currentFrameSize = full.Size;
                    chatBounds = resolved.Chat.Bounds;
                    using var chat = full.Clone(chatBounds, full.PixelFormat);
                    chat.Save(currentPath, ImageFormat.Png);
                }
                finally { File.Delete(captureResult.SavedPath); }
                frames++;
                Console.WriteLine($"\nFrame {frames}: {DateTimeOffset.Now:O}");
                Console.WriteLine($"Window size: {currentFrameSize.Width}x{currentFrameSize.Height}; Chat rectangle: {chatBounds}");
                if (previousPath is null) Console.WriteLine("Baseline established. OCR skipped.");
                else if (previousFrameSize != currentFrameSize || previousChatBounds != chatBounds)
                {
                    baselineResets++;
                    Console.WriteLine("Window size or resolved chat bounds changed. Baseline reset; OCR skipped for this frame.");
                }
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
                previousFrameSize = currentFrameSize;
                previousChatBounds = chatBounds;
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
            var summary = new { Status = status, FramesCaptured = frames, BaselineResets = baselineResets, ChangedFrames = changes, OcrCalls = ocrCalls };
            File.WriteAllText(Path.Combine(session, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Watch stopped: {status}; Frames={frames}; BaselineResets={baselineResets}; ChangedFrames={changes}; OCR calls={ocrCalls}");
        }
    }
}
