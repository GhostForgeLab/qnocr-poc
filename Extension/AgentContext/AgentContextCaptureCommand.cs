using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Adapter;
using QnocrPoc.Capture;
using QnocrPoc.Layout;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Bounded, read-only capture of explicitly marked test messages. No business dispatch.</summary>
public static class AgentContextCaptureCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        const string usage = "--agent-context-capture --database <file> --output <new-directory> --hwnd <hex> --prefix <unique-test-marker> --buyer-label <expected-observed-label> --frames <1..600> --interval-ms <1000..60000>";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length != 15) { Console.Error.WriteLine(usage); return 2; }
        for (var i = 1; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { Console.Error.WriteLine(usage); return 2; }
        if (!values.TryGetValue("--database", out var database) ||
            !values.TryGetValue("--output", out var output) ||
            !values.TryGetValue("--prefix", out var prefix) || prefix.Length < 6 ||
            !values.TryGetValue("--buyer-label", out var expectedBuyerLabel) ||
            !values.TryGetValue("--hwnd", out var handle) ||
            !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hwnd) || hwnd <= 0 ||
            !values.TryGetValue("--frames", out var frameText) || !int.TryParse(frameText, out var frames) || frames is < 1 or > 600 ||
            !values.TryGetValue("--interval-ms", out var intervalText) || !int.TryParse(intervalText, out var interval) || interval is < 1000 or > 60000)
        { Console.Error.WriteLine(usage); return 2; }
        var normalizer = new IdentityNormalizer();
        var normalizedExpectedBuyerLabel = normalizer.Normalize(expectedBuyerLabel);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Use a new output directory to keep capture evidence separate.");
        var capture = new QianniuWindowCapture();
        var window = capture.FindWindows(_ => { }).SingleOrDefault(w => w.Hwnd == (nint)hwnd &&
            w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected Qianniu reception window must be visible.");
        // Stable observation labels, never native platform IDs. HWND and capture time are excluded.
        var sellerId = ObservedId("seller", window.WindowTitle);
        Directory.CreateDirectory(output);
        using var pipeline = new AgentContextPipeline(database);
        var startedAt = DateTimeOffset.UtcNow;
        Write(Path.Combine(output, "session.json"), new
        {
            Version = "1.34.0-B", Mode = "ShadowContextOnly", StartedAt = startedAt,
            DatabasePath = pipeline.DatabasePath, TestPrefix = prefix, ExpectedBuyerLabel = expectedBuyerLabel, SellerId = sellerId,
            NormalizedExpectedBuyerLabel = normalizedExpectedBuyerLabel,
            WindowHandle = handle, WindowTitle = window.WindowTitle,
            IdentityLevel = "Observed", frames, interval,
            AiCalls = 0, IntentCalls = 0, KnowledgeCalls = 0, SendEnabled = false, SentMessages = 0
        });
        var native = new NativeWorkerClient();
        var structurer = new BuyerMessageStructurer();
        var envelopes = 0;
        var duplicateChecks = 0;
        var failedFrames = 0;
        var completedFrames = 0;
        var ocrCalls = 0;
        string? previousHash = null;
        Console.WriteLine($"CONTEXT_CAPTURE_READY: {output}; Prefix={prefix}; AI=OFF; Intent=OFF; Knowledge=OFF; Send=OFF");
        for (var frame = 1; frame <= frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(output, "stop.request"))) break;
            var current = capture.FindWindows(_ => { }).SingleOrDefault(w => w.Hwnd == window.Hwnd &&
                w.Pid == window.Pid && w.WindowTitle == window.WindowTitle && w.IsVisible && !w.IsMinimized && !w.IsCloaked);
            if (current is null) throw new InvalidOperationException("Selected reception window changed or became unavailable.");
            var slot = Path.Combine(output, "frames", frame.ToString("D4", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(slot);
            var result = await native.CaptureAsync(current, slot, cancellationToken);
            Write(Path.Combine(slot, "capture.json"), result);
            completedFrames++;
            if (!result.Success || result.SavedPath is null)
            {
                failedFrames++;
                Console.WriteLine($"CAPTURE_FAILED: frame={frame}; {result.Message}");
            }
            else
            {
                var layout = await new QianniuLayoutResolver().ResolveAsync(result.SavedPath);
                Write(Path.Combine(slot, "layout.json"), layout);
                new QianniuRegionExtractor().Extract(result.SavedPath, slot, layout.ToPixelLayout());
                var chat = Path.Combine(slot, "chat_region.png");
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(chat, cancellationToken)));
                if (hash != previousHash)
                {
                    var ocr = await native.RecognizeAsync(chat, cancellationToken);
                    ocrCalls++;
                    var batch = structurer.Structure(ocr);
                    Write(Path.Combine(slot, "buyer-messages.json"), batch);
                    var indices = Enumerable.Range(0, batch.Messages.Count)
                        .Where(index => batch.Messages[index].Text.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    var selected = batch with
                    {
                        Messages = indices.Select(index => batch.Messages[index]).ToArray(),
                        Evidence = indices.Select((index, ordinal) =>
                            batch.Evidence.Single(e => e.MessageIndex == index) with { MessageIndex = ordinal }).ToArray()
                    };
                    var buyerLabel = SessionRegistry.ReadBuyer(selected, ocr);
                    string? normalizedBuyerLabel = null;
                    if (buyerLabel is not null)
                    {
                        try { normalizedBuyerLabel = normalizer.Normalize(buyerLabel); }
                        catch (ArgumentException) { /* Invalid/empty OCR candidates remain Held. */ }
                    }
                    if (indices.Length > 0 && normalizedBuyerLabel == normalizedExpectedBuyerLabel)
                    {
                        // Normalize before the existing observation hash; a hash cannot repair label aliases.
                        var buyerId = ObservedId("buyer", normalizedBuyerLabel);
                        Write(Path.Combine(slot, "identity.json"), new { SellerId = sellerId,
                            BuyerId = buyerId, BuyerLabel = buyerLabel, NormalizedBuyerLabel = normalizedBuyerLabel,
                            IdentityLevel = "Observed" });
                        IMessageSource source = new OcrMessageSource(_ => Task.FromResult(selected),
                            sellerId, buyerId);
                        await foreach (var envelope in source.ReadAsync(cancellationToken))
                        {
                            var number = ++envelopes;
                            var envelopePath = Path.Combine(slot, $"envelope_{number:D4}.json");
                            Write(envelopePath, envelope);
                            var context = pipeline.ProcessMessage(envelope);
                            // A fresh DTO traverses Stable Identity and Manager again, without upstream dedupe.
                            var retry = JsonSerializer.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath))!;
                            var repeated = pipeline.ProcessMessage(retry);
                            if (!ReferenceEquals(context, repeated)) throw new InvalidDataException("Repeated envelope changed Context.");
                            duplicateChecks++;
                            Write(Path.Combine(slot, $"context_{number:D4}.json"), context);
                            Console.WriteLine($"CONTEXT_SAVED: frame={frame}; session={context.SessionId}; messages={context.Messages.Count}; duplicate=ignored");
                        }
                    }
                    else if (indices.Length > 0) Console.WriteLine($"IDENTITY_HELD: frame={frame}; OCR buyer header does not match the explicit test label");
                    previousHash = hash;
                }
            }
            if (frame < frames) await Task.Delay(interval, cancellationToken);
        }
        var sessions = new SQLiteAgentSessionStore(pipeline.DatabasePath).LoadSessions();
        Write(Path.Combine(output, "summary.json"), new
        {
            Mode = "ShadowContextOnly", StartedAt = startedAt, EndedAt = DateTimeOffset.UtcNow,
            CompletedFrames = completedFrames, FailedFrames = failedFrames, OcrCalls = ocrCalls,
            Envelopes = envelopes, DuplicateChecks = duplicateChecks, Sessions = sessions.Count,
            StoredMessages = sessions.Sum(s => s.Messages.Count),
            AiCalls = 0, IntentCalls = 0, KnowledgeCalls = 0, ReplyCalls = 0,
            SendEnabled = false, SentMessages = 0,
            Status = envelopes > 0 ? "Captured" : "NoControlledMessages"
        });
        Console.WriteLine($"CONTEXT_CAPTURE_END: Envelopes={envelopes}; DuplicateChecks={duplicateChecks}; Sessions={sessions.Count}; AI/Intent/Knowledge/Reply/SentMessages=0");
        return envelopes > 0 ? 0 : 3;
    }

    private static string ObservedId(string role, string label) =>
        "observed:" + role + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label))).ToLowerInvariant();

    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
