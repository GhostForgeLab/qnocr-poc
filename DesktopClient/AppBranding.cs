using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QNAgent;

internal static class AppBranding
{
    private static readonly Uri IconUri = new("pack://application:,,,/QNAgent;component/Assets/qn-agent.ico");
    private static readonly Lazy<ImageSource> WindowImage = new(() =>
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        var image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(frame => frame.PixelWidth).First();
        image.Freeze(); return image;
    });
    public static ImageSource WindowIcon => WindowImage.Value;
    public static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = Application.GetResourceStream(IconUri)!.Stream;
        using var source = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)source.Clone();
    }
}
