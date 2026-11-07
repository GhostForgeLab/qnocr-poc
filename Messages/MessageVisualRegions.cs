using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace QnocrPoc.Messages;

public static class MessageVisualRegions
{
    public static Rect[] LocateSeller(Mat image)
    {
        // Light-theme outgoing bubbles are pale blue and right aligned. Color + geometry
        // alone do not accept a message: the structurer still requires a reliable sender/time header.
        using var mask = new Mat(); using var difference = new Mat(); using var selected = new Mat();
        Cv2.InRange(image, new Scalar(225, 200, 175), new Scalar(255, 255, 255), mask);
        var channels = Cv2.Split(image);
        try
        {
            Cv2.Subtract(channels[0], channels[2], difference); Cv2.InRange(difference, new Scalar(12), new Scalar(255), selected); Cv2.BitwiseAnd(mask, selected, mask);
            Cv2.Subtract(channels[1], channels[2], difference); Cv2.InRange(difference, new Scalar(3), new Scalar(255), selected); Cv2.BitwiseAnd(mask, selected, mask);
            Cv2.Subtract(channels[0], channels[1], difference); Cv2.InRange(difference, new Scalar(0), new Scalar(45), selected); Cv2.BitwiseAnd(mask, selected, mask);
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours.Select(c => (Bounds: Cv2.BoundingRect(c), Area: Cv2.ContourArea(c)))
            .Where(c => c.Bounds.Right >= image.Width * .70 && c.Bounds.Right <= image.Width * .96 && c.Bounds.Left >= image.Width * .08 &&
                c.Bounds.Width >= 18 && c.Bounds.Height >= 20 && c.Area >= c.Bounds.Width * (double)c.Bounds.Height * .65)
            .Select(c => c.Bounds).OrderBy(r => r.Top).ThenBy(r => r.Left).ToArray();
    }
    public static Rect[] Locate(Mat image, bool includeColor = true)
    {
        using var white = new Mat();
        Cv2.InRange(image, new Scalar(250, 250, 250), new Scalar(255, 255, 255), white);
        Cv2.FindContours(white, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        bool Left(Rect r) => r.Left >= image.Width * .09 && r.Left <= image.Width * .30 && r.Right <= image.Width * .92 && r.Width >= 18 && r.Height >= 20;
        var bubbles = contours.Select(c => (Bounds: Cv2.BoundingRect(c), Area: Cv2.ContourArea(c)))
            .Where(c => Left(c.Bounds) && c.Area >= c.Bounds.Width * (double)c.Bounds.Height * .65)
            .Select(c => c.Bounds).ToList();
        if (!includeColor) return bubbles.OrderBy(b => b.Top).ThenBy(b => b.Left).ToArray();
        // Standalone colored stickers have no white bubble. Buyer/time proof is still mandatory.
        using var hsv = new Mat(); using var color = new Mat();
        Cv2.CvtColor(image, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, 70, 45), new Scalar(180, 255, 255), color);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        Cv2.MorphologyEx(color, color, MorphTypes.Close, kernel);
        Cv2.FindContours(color, out Point[][] colored, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var c in colored)
        {
            var r = Cv2.BoundingRect(c);
            if (Left(r) && Cv2.ContourArea(c) >= 60 && !bubbles.Any(b => (b & r).Width > 0 && (b & r).Height > 0)) bubbles.Add(r);
        }
        return bubbles.OrderBy(b => b.Top).ThenBy(b => b.Left).ToArray();
    }
    public static Rect OcrRegion(Mat image)
    {
        var regions = Locate(image).Concat(LocateSeller(image)).ToArray();
        if (regions.Length == 0) return new Rect(0, 0, image.Width, image.Height);
        var top = Math.Max(0, regions.Min(r => r.Top) - Math.Max(120, image.Height / 6));
        var bottom = Math.Min(image.Height, regions.Max(r => r.Bottom) + 12);
        return new Rect(0, top, image.Width, bottom - top);
    }
    public static bool HasInk(Mat crop)
    {
        using var gray = new Mat(); using var ink = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, ink, 225, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(ink) >= 12;
    }
    public static bool HasColor(Mat crop)
    {
        using var hsv = new Mat(); using var mask = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(0, 70, 45), new Scalar(180, 255, 255), mask);
        return Cv2.CountNonZero(mask) >= 20;
    }
    public static byte[] Preview(Mat crop)
    {
        var ratio = Math.Min(1, 512d / Math.Max(crop.Width, crop.Height));
        if (ratio == 1) return crop.ToBytes(".png");
        using var small = new Mat();
        Cv2.Resize(crop, small, new Size(Math.Max(1, (int)(crop.Width * ratio)), Math.Max(1, (int)(crop.Height * ratio))), 0, 0, InterpolationFlags.Area);
        return small.ToBytes(".png");
    }
}
