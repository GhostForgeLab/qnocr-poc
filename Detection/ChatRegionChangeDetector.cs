using System.Diagnostics;
using OpenCvSharp;
using QnocrPoc.Models;

namespace QnocrPoc.Detection;

/// <summary>Pixel comparison only. A visual change is not proof of a new chat message.</summary>
public sealed class ChatRegionChangeDetector
{
    public int PixelDifferenceThreshold { get; }
    public double MinimumDifferenceRatio { get; }
    public int MinimumChangedPixels { get; }

    public ChatRegionChangeDetector(int pixelDifferenceThreshold = 24,
        double minimumDifferenceRatio = 0.001, int minimumChangedPixels = 20)
    {
        if (pixelDifferenceThreshold is < 0 or > 254)
            throw new ArgumentOutOfRangeException(nameof(pixelDifferenceThreshold));
        if (!double.IsFinite(minimumDifferenceRatio) || minimumDifferenceRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumDifferenceRatio));
        if (minimumChangedPixels < 1) throw new ArgumentOutOfRangeException(nameof(minimumChangedPixels));
        PixelDifferenceThreshold = pixelDifferenceThreshold;
        MinimumDifferenceRatio = minimumDifferenceRatio;
        MinimumChangedPixels = minimumChangedPixels;
    }

    public ChangeDetectionResult Detect(string previousFramePath, string currentFramePath)
    {
        var stopwatch = Stopwatch.StartNew();
        using var previous = ReadFrame(previousFramePath);
        using var current = ReadFrame(currentFramePath);
        if (previous.Size() != current.Size())
            throw new ArgumentException($"Frame sizes differ: {previous.Width}x{previous.Height} vs {current.Width}x{current.Height}. No automatic resizing.");

        using var difference = new Mat();
        using var maximumChannel = new Mat();
        using var mask = new Mat();
        Cv2.Absdiff(previous, current, difference);
        var channels = Cv2.Split(difference);
        try
        {
            Cv2.Max(channels[0], channels[1], maximumChannel);
            Cv2.Max(maximumChannel, channels[2], maximumChannel);
            Cv2.Threshold(maximumChannel, mask, PixelDifferenceThreshold, 255, ThresholdTypes.Binary);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }

        var count = Cv2.CountNonZero(mask);
        var total = checked(current.Width * current.Height);
        var ratio = (double)count / total;
        var changed = count >= MinimumChangedPixels && ratio >= MinimumDifferenceRatio;
        System.Drawing.Rectangle? rectangle = null;
        if (changed)
        {
            using var points = new Mat();
            Cv2.FindNonZero(mask, points);
            var bounds = Cv2.BoundingRect(points);
            rectangle = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        stopwatch.Stop();
        return new(changed, ratio, rectangle, count, total, current.Width, current.Height,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static Mat ReadFrame(string path)
    {
        var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (!image.Empty()) return image;
        image.Dispose();
        throw new InvalidDataException($"Cannot decode frame: {path}");
    }
}
