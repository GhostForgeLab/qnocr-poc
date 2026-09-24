using System.Drawing;

namespace QnocrPoc.Models;

public sealed record ChangeDetectionResult(
    bool Changed,
    double DifferenceRatio,
    Rectangle? ChangedRectangle,
    int DifferentPixelCount,
    int TotalPixelCount,
    int FrameWidth,
    int FrameHeight,
    double ElapsedMilliseconds);
