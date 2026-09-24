using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Detection;

public sealed class ChatRegionChangePipeline
{
    private readonly ChatRegionChangeDetector _detector = new();

    public ChangeDetectionResult Process(string previousPath, string currentPath,
        string outputDirectory, Action<string> runOcr, Action<string> log)
    {
        var result = _detector.Detect(previousPath, currentPath);
        log($"Changed: {result.Changed} | DifferenceRatio: {result.DifferenceRatio:F6} | Pixels: {result.DifferentPixelCount}/{result.TotalPixelCount} | Detection: {result.ElapsedMilliseconds:F1} ms");
        if (!result.Changed)
        {
            log("OCR skipped: no significant pixel change.");
            return result;
        }

        var changed = result.ChangedRectangle!.Value;
        // Preserve complete horizontal text lines; the exact diff box remains in the result.
        var top = Math.Max(0, changed.Top - 12);
        var bottom = Math.Min(result.FrameHeight, changed.Bottom + 12);
        var ocrRectangle = new Rectangle(0, top, result.FrameWidth, bottom - top);
        var directory = Path.GetFullPath(Path.Combine(outputDirectory,
            $"change_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        File.Copy(previousPath, Path.Combine(directory, "previous.png"));
        File.Copy(currentPath, Path.Combine(directory, "current.png"));
        var cropPath = Path.Combine(directory, "changed_region.png");
        using (var current = new Bitmap(currentPath))
        using (var crop = current.Clone(ocrRectangle, current.PixelFormat))
            crop.Save(cropPath, ImageFormat.Png);

        void SaveReport(string status) => File.WriteAllText(Path.Combine(directory, "change.json"),
            JsonSerializer.Serialize(new
            {
                Status = status,
                Detection = result,
                _detector.PixelDifferenceThreshold,
                _detector.MinimumDifferenceRatio,
                _detector.MinimumChangedPixels,
                OcrRectangle = ocrRectangle,
                OcrBoundsOriginInChat = new { X = ocrRectangle.X, Y = ocrRectangle.Y },
                Meaning = "Visual change only; not confirmed new message. OCR bounds are relative to changed_region.png."
            }, new JsonSerializerOptions { WriteIndented = true }));

        SaveReport("PendingOcr");
        log($"ChangedRectangle: {changed}\nOCR context rectangle: {ocrRectangle}\nSaved change: {cropPath}");
        try
        {
            runOcr(cropPath);
            SaveReport("OcrCompleted");
        }
        catch
        {
            SaveReport("OcrFailed");
            throw;
        }
        return result;
    }
}
