using System.Diagnostics;
using OpenCvSharp;
using QnocrPoc.Models;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;
using QnocrPoc.Messages;

namespace QnocrPoc.OCR;

/// <summary>One local CPU OCR call. No HTTP requests, polling, or message interpretation.</summary>
public sealed class PaddleOcrService(int cpuThreads = 0) : IDisposable
{
    private PaddleOcrAll? _engine;
    public OcrResult Recognize(string imagePath)
    {
        var path = Path.GetFullPath(imagePath);
        if (!File.Exists(path)) throw new FileNotFoundException("OCR input image was not found.", path);
        using var session = new PaddleOcrService();
        return session.Recognize(File.ReadAllBytes(path), path);
    }

    // Worker owns one instance, used sequentially; models stay warm between changed frames.
    public OcrResult Recognize(byte[] bytes, string sourceId, bool focusBuyer = false)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.WorkingSet64;
        var total = Stopwatch.StartNew();
        // Decode bytes rather than using native file APIs, so Chinese paths work too.
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("OCR input is not a decodable image.");
        var roi = focusBuyer ? MessageVisualRegions.OcrRegion(image) : new Rect(0, 0, image.Width, image.Height);
        using var input = new Mat(image, roi);

        var initialization = Stopwatch.StartNew();
        _engine ??= new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Mkldnn(cpuMathThreadCount: cpuThreads))
        {
            AllowRotateDetection = false,
            Enable180Classification = false
        };
        initialization.Stop();
        var recognition = Stopwatch.StartNew();
        var raw = _engine.Run(input);
        recognition.Stop();

        // A simple top-to-bottom/left-to-right display order, not chat role or message extraction.
        var lines = raw.Regions.Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .OrderBy(region => region.Rect.Center.Y)
            .ThenBy(region => region.Rect.Center.X)
            .Select((region, index) =>
            {
                var bounds = region.Rect.BoundingRect();
                return new OcrLine(index + 1, region.Text, float.IsFinite(region.Score) ? region.Score : null,
                    new OcrBounds(bounds.X + roi.X, bounds.Y + roi.Y, bounds.Width, bounds.Height));
            }).ToArray();
        total.Stop();
        process.Refresh();
        return new(sourceId, "PP-OCRv5 Chinese (Sdcb.PaddleOCR 3.3.1, CPU MKL)", lines,
            raw.Regions.Length - lines.Length, lines.Average(line => (double?)line.Confidence),
            initialization.Elapsed.TotalMilliseconds, recognition.Elapsed.TotalMilliseconds,
            total.Elapsed.TotalMilliseconds, before, process.WorkingSet64, process.PeakWorkingSet64);
    }
    public void Dispose() { _engine?.Dispose(); _engine = null; }
}
