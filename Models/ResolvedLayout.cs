using System.Drawing;

namespace QnocrPoc.Models;

public sealed record ResolvedRegion(Rectangle Bounds, string Source, string Reason);
public sealed record UiaLayoutNode(int Id, int ParentId, int Depth, string ControlType, Rectangle Bounds);
public sealed record UiaGeometrySnapshot(bool Complete, string Status, Rectangle WindowBounds, IReadOnlyList<UiaLayoutNode> Nodes);

public sealed record ResolvedLayout(Size ScreenshotSize, ResolvedRegion BuyerList, ResolvedRegion Chat,
    ResolvedRegion Product, ResolvedRegion Input, UiaGeometrySnapshot Uia,
    IReadOnlyList<string> Diagnostics, double ElapsedMilliseconds)
{
    public QianniuLayout ToPixelLayout() => new(ScreenshotSize, BuyerList.Bounds, Chat.Bounds, Product.Bounds, Input.Bounds);
    public IReadOnlyList<(string Name, ResolvedRegion Region)> GetRegions() =>
        [("BuyerList", BuyerList), ("Chat", Chat), ("Product", Product), ("Input", Input)];
}
