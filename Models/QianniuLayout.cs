using System.Drawing;

namespace QnocrPoc.Models;

/// <summary>
/// Fixed physical-pixel rectangles relative to the full PNG's top-left corner.
/// Manually calibrated against the 1638x1100 reception-center capture from 2026-09-24.
/// No scaling, OCR, or visual detection is performed.
/// </summary>
public sealed class QianniuLayout
{
    public Size ScreenshotSize { get; } = new(1638, 1100);
    public Rectangle BuyerListRegion { get; init; } = new(80, 120, 300, 980);
    public Rectangle ChatRegion { get; init; } = new(380, 181, 654, 537);
    public Rectangle ProductRegion { get; init; } = new(1034, 120, 604, 924);
    public Rectangle InputRegion { get; init; } = new(380, 718, 654, 382);

    public IReadOnlyList<(string FileName, Rectangle Bounds)> GetRegions() =>
    [
        ("chat_region.png", ChatRegion),
        ("buyer_list_region.png", BuyerListRegion),
        ("product_region.png", ProductRegion),
        ("input_region.png", InputRegion)
    ];
}
