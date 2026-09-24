using System.Drawing;

namespace QnocrPoc.Models;

/// <summary>
/// Resolved pixel rectangles for one particular screenshot, calculated from ratios.
/// </summary>
public sealed record QianniuLayout(
    Size ScreenshotSize,
    Rectangle BuyerListRegion,
    Rectangle ChatRegion,
    Rectangle ProductRegion,
    Rectangle InputRegion)
{
    public IReadOnlyList<(string FileName, Rectangle Bounds)> GetRegions() =>
    [
        ("chat_region.png", ChatRegion),
        ("buyer_list_region.png", BuyerListRegion),
        ("product_region.png", ProductRegion),
        ("input_region.png", InputRegion)
    ];
}
