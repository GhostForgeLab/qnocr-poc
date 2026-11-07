using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using QnocrPoc.Messages;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class RecognitionImageChecks
{
    internal static async Task<int> ReplayAsync(string path)
    {
        using var supplied = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (supplied.Width < 650 || supplied.Height < 700) throw new InvalidDataException("Unexpected reported screenshot size.");
        // This attachment contains a partial desktop and the assistant. Use its explicitly visible
        // conversation panel for reproduction; do not treat the full desktop as a captured HWND.
        using var chat = new Mat(supplied, new Rect(0, 190, 650, 505)); var bytes = chat.ToBytes(".png");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var worker = await MemoryNativeWorker.StartAsync(timeout.Token);
        var ocr = await worker.RecognizeChatAsync(bytes, "memory:user-supplied-reproduction", timeout.Token);
        var batch = new BuyerMessageStructurer(true, true).Structure(ocr, bytes);
        Console.WriteLine($"Attachment chat: buyer={batch.Messages.Count(m => m.Role == "buyer")}, seller={batch.Messages.Count(m => m.Role == "seller")}, bound={SessionRegistry.ReadBuyer(batch, ocr) is not null}");
        if (batch.Messages.Count(m => m.Role == "buyer") != 1 || batch.Messages.Single(m => m.Role == "buyer").Text != "好的" || SessionRegistry.ReadBuyer(batch, ocr) is null)
        {
            Console.WriteLine("Bubble geometry: " + JsonSerializer.Serialize(MessageVisualRegions.Locate(chat).Select(r => new { r.X, r.Y, r.Width, r.Height })));
            Console.WriteLine("Review reasons: " + JsonSerializer.Serialize(batch.ReviewItems.GroupBy(r => r.Reason).Select(g => new { Reason = g.Key, Count = g.Count() })));
            throw new InvalidDataException("Reported screenshot buyer recognition regression.");
        }
        Console.WriteLine("PASS: reported short buyer message and split, joined-date header are accepted without weakening identity checks.");
        Console.WriteLine("No external model called; no new screenshot or transcript files created.");
        return 0;
    }
}
