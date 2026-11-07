using System.Security.Cryptography;
using OpenCvSharp;
using QnocrPoc.Layout;
using QnocrPoc.Models;

namespace QnocrPoc.Capture;

public sealed record MemoryChatFrame(byte[] Chat, byte[] Product, string Fingerprint, QianniuLayout Layout)
{
    public string ChatFingerprint { get; init; } = Fingerprint;
    public string IdentityFingerprint { get; init; } = "";
    public static MemoryChatFrame FromPng(byte[] png, bool includeProduct = true)
    {
        using var image = Cv2.ImDecode(png, ImreadModes.Color);
        if (image.Empty()) throw new InvalidDataException("无法解码内存画面。");
        var layout = new QianniuLayoutResolver().Resolve(image).ToPixelLayout();
        byte[] Crop(System.Drawing.Rectangle bounds)
        {
            using var crop = new Mat(image, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            return crop.ToBytes(".png");
        }
        var chat = Crop(layout.ChatRegion);
        var product = includeProduct ? Crop(layout.ProductRegion) : Array.Empty<byte>();
        // Watch the header too: a different buyer can have visually identical message bubbles.
        // Exclude the input caret and other buyers' unread counters from the fingerprint.
        var header = Crop(System.Drawing.Rectangle.FromLTRB(layout.ChatRegion.Left, layout.BuyerListRegion.Top,
            layout.ChatRegion.Right, layout.ChatRegion.Top));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(chat); digest.AppendData(header);
        var chatFingerprint = Convert.ToHexString(digest.GetHashAndReset());
        digest.AppendData(chat); digest.AppendData(header); digest.AppendData(product);
        return new(chat, product, Convert.ToHexString(digest.GetHashAndReset()), layout)
        { ChatFingerprint = chatFingerprint, IdentityFingerprint = Convert.ToHexString(SHA256.HashData(header)) };
    }
}
