using System.Drawing;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using QnocrPoc.Adapter;
using QnocrPoc.Capture;
using QnocrPoc.Detection;
using QnocrPoc.Layout;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.OCR;
using QnocrPoc.Product;

namespace QnocrPoc.Shadow;

public sealed record ShadowRunOptions(int? MaximumFrames = null, int IntervalMilliseconds = 1000, string? IdentityMappingPath = null);
public sealed record ShadowRunSummary(string Status, int Attempts, int SuccessfulFrames, int FailedFrames,
    int ProductOcrCalls, int ChatOcrCalls, int Envelopes, int ConvertedEnvelopes, int DeferredEnvelopes, string SessionDirectory);

public sealed class QnocrShadowRunner
{
    private readonly Func<string, Task<CaptureResult>>? _capture;
    private readonly Func<string, QianniuWindow?, Task<ResolvedLayout>> _layout;
    private readonly Func<string, OcrResult> _ocr;
    private readonly Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>> _adapt;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public QnocrShadowRunner(Func<string, Task<CaptureResult>>? capture = null,
        Func<string, QianniuWindow?, Task<ResolvedLayout>>? layout = null, Func<string, OcrResult>? ocr = null,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>>? adapt = null)
    {
        _capture = capture;
        _layout = layout ?? new QianniuLayoutResolver().ResolveAsync;
        _ocr = ocr ?? new PaddleOcrService().Recognize;
        _adapt = adapt ?? new ShadowAdapterClient().ConvertAsync;
    }

    public async Task<ShadowRunSummary> RunAsync(ShadowRunOptions options, CancellationToken token = default)
    {
        if (options.MaximumFrames is <= 0 || options.IntervalMilliseconds is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(options));
        token.ThrowIfCancellationRequested();
        var session = Path.GetFullPath(Path.Combine("Output", "Shadow", $"shadow_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(session);
        string? identityPath = null;
        var sellerId = "";
        var buyerId = "";
        var isTestIdentity = false;
        if (options.IdentityMappingPath is not null)
        {
            var identityJson = await File.ReadAllTextAsync(options.IdentityMappingPath, token);
            using var identity = JsonDocument.Parse(identityJson);
            sellerId = identity.RootElement.GetProperty("SellerId").GetString() ?? "";
            buyerId = identity.RootElement.GetProperty("BuyerId").GetString() ?? "";
            isTestIdentity = identity.RootElement.GetProperty("IsTestMapping").GetBoolean();
            if (string.IsNullOrWhiteSpace(sellerId) || string.IsNullOrWhiteSpace(buyerId)) throw new InvalidDataException("Explicit identity mapping must contain seller/buyer IDs.");
            identityPath = Path.Combine(session, "identity.snapshot.json");
            await File.WriteAllTextAsync(identityPath, identityJson, token);
        }

        var capture = _capture;
        QianniuWindow? window = null;
        if (capture is null)
        {
            var windows = new QianniuWindowCapture();
            window = windows.FindWindows(Console.WriteLine).FirstOrDefault(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Show a visible Qianniu reception center before starting Shadow.");
            capture = directory => windows.CaptureAsync(window, directory);
        }
        Save(Path.Combine(session, "session.json"), new { Mode = "ShadowOnly", StartedAt = DateTimeOffset.Now,
            options.MaximumFrames, options.IntervalMilliseconds, IsTestIdentity = isTestIdentity,
            IdentityBinding = identityPath is null ? "Unavailable" : "OperatorProvidedConversationUnverified",
            Window = window is null ? null : new { window.ProcessName, window.Pid, Hwnd = $"0x{window.Hwnd:X}", window.WindowTitle, window.WindowBounds } });
        var profiles = new ProductContextResolver(Path.Combine(AppContext.BaseDirectory, "Product", "product-profiles.sample.json"));
        var detector = new ChatRegionChangeDetector();
        var structurer = new BuyerMessageStructurer();
        var extractor = new QianniuRegionExtractor();
        string? previousChat = null, cachedProductHash = null;
        QianniuLayout? previousLayout = null;
        ProductContext? product = null;
        OcrResult? productOcr = null;
        var productEvidenceFrame = 0;
        var attempts = 0; var frames = 0; var failures = 0; var productCalls = 0; var chatCalls = 0; var envelopes = 0; var converted = 0; var deferred = 0;
        var status = "Completed";
        Console.WriteLine($"[SHADOW ONLY] Session: {session}\nIdentity: {(identityPath is null ? "Unavailable" : isTestIdentity ? "Explicit TEST mapping, not real account identity" : "Operator mapping; current conversation unverified")}\nFirst frame is a historical baseline snapshot. No AI, SalesAgent, coordinator or send consumer is connected.");
        try
        {
            while (options.MaximumFrames is null || attempts < options.MaximumFrames)
            {
                token.ThrowIfCancellationRequested();
                attempts++;
                var at = DateTimeOffset.Now;
                string? fullPath = null;
                try
                {
                    var slot = Path.Combine(session, "scratch", "slot_" + (attempts % 2));
                    Directory.CreateDirectory(slot);
                    var captured = await capture(slot);
                    fullPath = captured.SavedPath;
                    // PrintWindow cannot be cancelled: stop instead of accumulating blocked native workers.
                    if (captured.TimedOut) { status = "CaptureTimedOut"; throw new TimeoutException(captured.Message); }
                    if (!captured.Success || fullPath is null) throw new InvalidOperationException(captured.Message);
                    var layout = await _layout(fullPath, window);
                    var pixels = layout.ToPixelLayout();
                    extractor.Extract(fullPath, slot, pixels);
                    var productPath = Path.Combine(slot, "product_region.png");
                    var chatPath = Path.Combine(slot, "chat_region.png");
                    var productHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(productPath, token)));
                    var cacheHit = productOcr is not null && productHash == cachedProductHash && previousLayout?.ProductRegion == pixels.ProductRegion;
                    if (!cacheHit)
                    {
                        token.ThrowIfCancellationRequested();
                        productCalls++;
                        productOcr = _ocr(productPath);
                        product = profiles.Resolve(productOcr);
                        cachedProductHash = productHash;
                        productEvidenceFrame = attempts;
                    }
                    var reset = previousLayout is null || previousLayout.ScreenshotSize != pixels.ScreenshotSize || previousLayout.ChatRegion != pixels.ChatRegion;
                    var change = reset || previousChat is null ? null : detector.Detect(previousChat, chatPath);
                    var kind = previousChat is null ? "BaselineSnapshot" : reset ? "LayoutResetSnapshot" : change!.Changed ? "ChangedSnapshot" : "Unchanged";
                    var adapterResults = new List<ShadowAdapterResult>();
                    var messageCount = 0; var reviewCount = 0;
                    string? eventDirectory = null;
                    if (reset || change!.Changed)
                    {
                        token.ThrowIfCancellationRequested();
                        eventDirectory = Path.Combine(session, $"frame_{attempts:D6}");
                        Directory.CreateDirectory(eventDirectory);
                        File.Copy(fullPath, Path.Combine(eventDirectory, "window.png"));
                        foreach (var (file, _) in pixels.GetRegions()) File.Copy(Path.Combine(slot, file), Path.Combine(eventDirectory, file));
                        var pinnedChat = Path.Combine(eventDirectory, "chat_region.png");
                        chatCalls++;
                        var chatOcr = _ocr(pinnedChat);
                        var batch = structurer.Structure(chatOcr, product!.ProductId ?? "", "SameCaptureVisibleProductNotVerifiedMessageAssociation");
                        messageCount = batch.Messages.Count; reviewCount = batch.ReviewItems.Count;
                        Save(Path.Combine(eventDirectory, "layout.json"), layout);
                        Save(Path.Combine(eventDirectory, "chat-ocr.json"), chatOcr);
                        Save(Path.Combine(eventDirectory, "product-ocr.json"), productOcr! with { ImagePath = Path.Combine(eventDirectory, "product_region.png") });
                        Save(Path.Combine(eventDirectory, "buyer-messages.json"), batch);
                        Save(Path.Combine(eventDirectory, "product-context.json"), product);
                        IMessageSource source = new OcrMessageSource(_ => Task.FromResult(batch), sellerId, buyerId);
                        var index = 0;
                        await foreach (var envelope in source.ReadAsync(token))
                        {
                            envelopes++;
                            var envelopePath = Path.Combine(eventDirectory, $"envelope_{++index:D3}.json");
                            Save(envelopePath, envelope);
                            var receipt = await _adapt(envelopePath, identityPath, Path.Combine(eventDirectory, $"adapter_{index:D3}"), token);
                            adapterResults.Add(receipt);
                            if (receipt.Status == "Converted") converted++; else deferred++;
                        }
                    }
                    frames++;
                    Append(session, new { Frame = attempts, CapturedAt = at, Mode = "ShadowOnly", Kind = kind, IsNewMessage = false,
                        IsTestIdentity = isTestIdentity, IdentityBinding = identityPath is null ? "Unavailable" : "OperatorProvidedConversationUnverified",
                        ChatBounds = pixels.ChatRegion, ProductBounds = pixels.ProductRegion, LayoutSources = layout.GetRegions().Select(x => new { x.Name, x.Region.Source }),
                        ProductStatus = product!.Status.ToString(), product.ProductId, ProductCacheHit = cacheHit, ProductEvidenceFrame = productEvidenceFrame,
                        Change = change, ConfirmedMessages = messageCount, ReviewItems = reviewCount, EventDirectory = eventDirectory, AdapterResults = adapterResults });
                    Console.WriteLine($"[Shadow] frame={attempts} {kind}; product={product.Status}; productCache={cacheHit}; messages={messageCount}; adapter={string.Join(',', adapterResults.Select(x => x.Status))}");
                    previousChat = chatPath;
                    previousLayout = pixels;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures++;
                    previousChat = null; previousLayout = null; cachedProductHash = null; productOcr = null; product = null;
                    Append(session, new { Frame = attempts, CapturedAt = at, Mode = "ShadowOnly", Kind = "FrameFailed", ErrorType = ex.GetType().Name, Error = ex.Message });
                    Console.WriteLine($"[Shadow] frame={attempts} failed: {ex.GetType().Name}: {ex.Message}");
                    if (status == "CaptureTimedOut") break;
                }
                finally { if (fullPath is not null && File.Exists(fullPath)) File.Delete(fullPath); }
                if (options.MaximumFrames is null || attempts < options.MaximumFrames) await Task.Delay(options.IntervalMilliseconds, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "Cancelled"; }
        catch { status = "Failed"; throw; }
        finally
        {
            if (status == "Completed" && (failures > 0 || deferred > 0)) status = "CompletedWithIssues";
            var summary = new ShadowRunSummary(status, attempts, frames, failures, productCalls, chatCalls, envelopes, converted, deferred, session);
            Save(Path.Combine(session, "summary.json"), summary);
            Console.WriteLine($"[Shadow] stopped: {JsonSerializer.Serialize(summary)}");
        }
        return new(status, attempts, frames, failures, productCalls, chatCalls, envelopes, converted, deferred, session);
    }

    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    private static void Append<T>(string session, T value) => File.AppendAllText(Path.Combine(session, "shadow.jsonl"), JsonSerializer.Serialize(value) + Environment.NewLine);
}
