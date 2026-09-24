using System.Drawing;

namespace QnocrPoc.Models;

/// <summary>Normalized layout (0..1), derived from the V0.2 reference screenshot.</summary>
public sealed class QianniuLayoutRatio
{
    private const double BuyerColumn = 0.04884004884004884;
    private const double ChatColumn = 0.231990231990232;
    private const double ProductColumn = 0.6312576312576312;
    private const double PanelTop = 0.10909090909090909;
    private const double MessageTop = 0.16454545454545455;
    private const double EditorTop = 0.6527272727272727;
    private const double ProductBottom = 0.9490909090909091;

    public double BuyerListLeft { get; init; } = BuyerColumn;
    public double BuyerListTop { get; init; } = PanelTop;
    public double BuyerListWidth { get; init; } = ChatColumn - BuyerColumn;
    public double BuyerListHeight { get; init; } = 1 - PanelTop;
    public double ChatLeft { get; init; } = ChatColumn;
    public double ChatTop { get; init; } = MessageTop;
    public double ChatWidth { get; init; } = ProductColumn - ChatColumn;
    public double ChatHeight { get; init; } = EditorTop - MessageTop;
    public double ProductLeft { get; init; } = ProductColumn;
    public double ProductTop { get; init; } = PanelTop;
    public double ProductWidth { get; init; } = 1 - ProductColumn;
    public double ProductHeight { get; init; } = ProductBottom - PanelTop;
    public double InputLeft { get; init; } = ChatColumn;
    public double InputTop { get; init; } = EditorTop;
    public double InputWidth { get; init; } = ProductColumn - ChatColumn;
    public double InputHeight { get; init; } = 1 - EditorTop;

    public QianniuLayout Resolve(Size screenshotSize)
    {
        if (screenshotSize.Width <= 0 || screenshotSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(screenshotSize), "Screenshot dimensions must be positive.");
        Rectangle Region(string name, double left, double top, double width, double height)
        {
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) ||
                left < 0 || top < 0 || width <= 0 || height <= 0 || left > 1 || top > 1 ||
                left + width > 1 + 1e-12 || top + height > 1 + 1e-12)
                throw new ArgumentOutOfRangeException(name, "Region ratios must be finite, positive-sized, and within [0,1].");

            // Round edges, not width/height independently; shared edges stay aligned.
            static int Edge(double value, int extent) => (int)Math.Round(Math.Clamp(value, 0, 1) * extent, MidpointRounding.AwayFromZero);
            var rectangle = Rectangle.FromLTRB(Edge(left, screenshotSize.Width), Edge(top, screenshotSize.Height),
                Edge(left + width, screenshotSize.Width), Edge(top + height, screenshotSize.Height));
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new ArgumentException($"{name} rounds to an empty region at this image size.", nameof(screenshotSize));
            return rectangle;
        }

        return new(screenshotSize,
            Region(nameof(BuyerListLeft), BuyerListLeft, BuyerListTop, BuyerListWidth, BuyerListHeight),
            Region(nameof(ChatLeft), ChatLeft, ChatTop, ChatWidth, ChatHeight),
            Region(nameof(ProductLeft), ProductLeft, ProductTop, ProductWidth, ProductHeight),
            Region(nameof(InputLeft), InputLeft, InputTop, InputWidth, InputHeight));
    }
}
