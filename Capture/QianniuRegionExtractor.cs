using System.Drawing;
using System.Drawing.Imaging;
using QnocrPoc.Models;

namespace QnocrPoc.Capture;

public sealed class QianniuRegionExtractor
{
    /// <summary>
    /// Writes four PNGs using exact pixel coordinates. Existing region outputs are replaced.
    /// Validates every rectangle before writing; leaves the source PNG unchanged.
    /// </summary>
    public IReadOnlyList<string> Extract(string screenshotPath, string outputDirectory, QianniuLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var sourcePath = Path.GetFullPath(screenshotPath);
        var targetDirectory = Path.GetFullPath(outputDirectory);
        using var source = new Bitmap(sourcePath);
        if (source.RawFormat.Guid != ImageFormat.Png.Guid)
            throw new ArgumentException("The source screenshot must be a PNG.", nameof(screenshotPath));
        if (source.Size != layout.ScreenshotSize)
            throw new ArgumentException(
                $"Fixed layout requires {layout.ScreenshotSize.Width}x{layout.ScreenshotSize.Height}; " +
                $"source is {source.Width}x{source.Height}. No automatic scaling is applied.", nameof(screenshotPath));

        var regions = layout.GetRegions();
        foreach (var (fileName, bounds) in regions)
        {
            // Use long arithmetic to reject even coordinates that would overflow Rectangle.Right/Bottom.
            if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
                (long)bounds.X + bounds.Width > source.Width || (long)bounds.Y + bounds.Height > source.Height)
                throw new ArgumentOutOfRangeException(nameof(layout), $"{fileName} is outside the screenshot: {bounds}.");
            if (string.Equals(sourcePath, Path.Combine(targetDirectory, fileName), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The source PNG cannot also be a region output file.", nameof(screenshotPath));
        }

        Directory.CreateDirectory(targetDirectory);
        var savedPaths = new List<string>();
        foreach (var (fileName, bounds) in regions)
        {
            var path = Path.Combine(targetDirectory, fileName);
            using var cropped = source.Clone(bounds, source.PixelFormat);
            cropped.Save(path, ImageFormat.Png);
            savedPaths.Add(path);
        }
        return savedPaths;
    }
}
