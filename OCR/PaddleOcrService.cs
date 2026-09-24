using System.Diagnostics;
using OpenCvSharp;
using QnocrPoc.Models;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace QnocrPoc.OCR;

/// <summary>One local CPU OCR call. No HTTP requests, polling, or message interpretation.</summary>
public sealed class PaddleOcrService
{
    public OcrResult Recognize(string imagePath)
    {
        var path = Path.GetFullPath(imagePath);
        if (!File.Exists(path)) throw new FileNotFoundException("OCR input image was not found.", path);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var before = process.WorkingSet64;
        var total = Stopwatch.StartNew();
        // Decode bytes rather than using native file APIs, so Chinese paths work too.
        using var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("OCR input is not a decodable image.");

        var initialization = Stopwatch.StartNew();
        using var engine = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Mkldnn())
        {
            AllowRotateDetection = false,
            Enable180Classification = false
        };
        initialization.Stop();
        var recognition = Stopwatch.StartNew();
        var raw = engine.Run(image);
        recognition.Stop();

        // A simple top-to-bottom/left-to-right display order, not chat role or message extraction.
        var lines = raw.Regions.Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .OrderBy(region => region.Rect.Center.Y)
            .ThenBy(region => region.Rect.Center.X)
            .Select((region, index) =>
            {
                var bounds = region.Rect.BoundingRect();
                return new OcrLine(index + 1, region.Text, float.IsFinite(region.Score) ? region.Score : null,
                    new OcrBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }).ToArray();
        total.Stop();
        process.Refresh();
        return new(path, "PP-OCRv5 Chinese (Sdcb.PaddleOCR 3.3.1, CPU MKL)", lines,
            raw.Regions.Length - lines.Length, lines.Average(line => (double?)line.Confidence),
            initialization.Elapsed.TotalMilliseconds, recognition.Elapsed.TotalMilliseconds,
            total.Elapsed.TotalMilliseconds, before, process.WorkingSet64, process.PeakWorkingSet64);
    }
}
