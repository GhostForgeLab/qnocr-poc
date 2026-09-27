using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using QnocrPoc.Capture;
using QnocrPoc.Models;
using QnocrPoc.OCR;
using QnocrPoc.Detection;
using QnocrPoc.Layout;
using QnocrPoc.Product;
using QnocrPoc.Messages;
using QnocrPoc.Adapter;
using QnocrPoc.Shadow;
using QnocrPoc.Knowledge;
using QnocrPoc.Decision;

// Owned one-shot native workers: parent kills only these helpers on timeout. No UI/send action.
if (args.Length == 4 && args[0] == "--capture-worker")
{
    var hwnd = (nint)long.Parse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    var pid = int.Parse(args[2], CultureInfo.InvariantCulture);
    var capture = new QianniuWindowCapture();
    var window = capture.FindWindows(_ => { }).Single(w => w.Hwnd == hwnd && w.Pid == pid && w.IsVisible && !w.IsMinimized && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal));
    var result = await capture.CaptureAsync(window, args[3]);
    File.WriteAllText(Path.Combine(args[3], "capture-result.json"), JsonSerializer.Serialize(result));
    return 0;
}
if (args.Length == 3 && args[0] == "--ocr-worker")
{
    var result = new PaddleOcrService().Recognize(args[1]);
    File.WriteAllText(args[2], JsonSerializer.Serialize(result));
    return 0;
}

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("[QN OCR POC V1.28]");
Console.WriteLine($"Time: {DateTimeOffset.Now:O}");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var operation = "Capture";

try
{
    if (args.Length > 0 && args[0].StartsWith("--assist-batch-", StringComparison.Ordinal))
    {
        operation = "Evaluation batch";
        return EvaluationBatchCommand.Run(args);
    }

    if (args.Length > 0 && args[0] is "--assist-dashboard" or "--assist-stats" or "--assist-sessions" or "--assist-samples" or "--assist-classifications" or "--assist-classify" or "--assist-feedback" or "--assist-list" or "--assist-show" or "--assist-edit" or "--assist-approve" or "--assist-copy" or "--assist-outcome")
    {
        operation = "Assistant reply review";
        return AssistantCommand.Run(args);
    }

    if (args.Length > 0 && args[0] is "--preview" or "--confirm-send" or "--cancel-preview")
    {
        operation = "Human send confirmation";
        return await ConfirmSendCommand.RunAsync(args);
    }

    if (args.Length > 0 && args[0] is "--knowledge-draft" or "--knowledge-show" or "--knowledge-review")
    {
        operation = "Knowledge authoring";
        return KnowledgeAuthoringCommand.Run(args);
    }

    if (args.Length > 0 && args[0] is "--knowledge-import" or "--knowledge-revision" or
        "--knowledge-revision-show" or "--knowledge-revision-publish")
    {
        operation = "Knowledge import/revision";
        return KnowledgeImportRevisionCommand.Run(args);
    }

    if (args.Length > 0 && args[0] == "--compare-intents")
    {
        operation = "JEV intent comparison";
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: --compare-intents SAMPLES_JSON JEV_CONFIG_JSON OUTPUT_DIR");
            return 2;
        }
        var samples = JsonSerializer.Deserialize<List<IntentSample>>(File.ReadAllText(args[1]))
            ?? throw new InvalidDataException("Intent samples are null.");
        var config = JEVProviderConfig.Load(args[2]);
        using var jev = new JEVIntentClassifier(config);
        var comparisons = new IntentComparisonRunner(new RuleBasedIntentClassifier(), jev)
            .Compare(samples, args[3]);
        var matched = comparisons.Count(item => item.Match == true);
        var different = comparisons.Count(item => item.Match == false);
        var unavailable = comparisons.Count(item => item.Match is null);
        Console.WriteLine($"[Intent Comparison] total={comparisons.Count} matched={matched} different={different} unavailable={unavailable}; Output={Path.GetFullPath(args[3])}; AgentRuleBased=UNCHANGED; Send=OFF");
        return unavailable == 0 ? 0 : 3;
    }
    if (args.Length > 0 && args[0] == "--intent-review-list")
    {
        operation = "Intent human review";
        if (args.Length != 2) return 2;
        Console.WriteLine(JsonSerializer.Serialize(new IntentQualityReviewService(args[1]).List(),
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length > 0 && args[0] == "--intent-review-mark")
    {
        operation = "Intent human review";
        if (args.Length != 5) return 2;
        Console.WriteLine(JsonSerializer.Serialize(new IntentQualityReviewService(args[1])
            .Mark(args[2], args[3], args[4])));
        return 0;
    }
    if (args.Length > 0 && args[0] == "--shadow")
    {
        int? frames = null;
        var interval = 1000;
        string? identity = null;
        var aiShadow = false;
        double? minutes = null;
        string? stateDirectory = null;
        string? aiSession = null;
        string? providerConfig = null;
        string? productKnowledge = null;
        string? knowledgeItems = null;
        var intentProviderConfig = Path.Combine("Config", "intent-provider.json");
        var maxRunMb = 1024.0; var maxStateMb = 4096.0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!seen.Add(args[i])) { Console.Error.WriteLine("Duplicate shadow option."); return 2; }
            if (args[i] == "--ai-shadow") { aiShadow = true; continue; }
            if (i + 1 >= args.Length) { Console.Error.WriteLine("Missing shadow option value."); return 2; }
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n) && n > 0) frames = n;
            else if (args[i] == "--interval-ms" && int.TryParse(args[i + 1], out var ms) && ms is >= 100 and <= 60000) interval = ms;
            else if (args[i] == "--identity" && !string.IsNullOrWhiteSpace(args[i + 1])) identity = args[i + 1];
            else if (args[i] == "--minutes" && double.TryParse(args[i + 1], out var duration) && double.IsFinite(duration) && duration > 0) minutes = duration;
            else if (args[i] == "--state-dir" && !string.IsNullOrWhiteSpace(args[i + 1])) stateDirectory = args[i + 1];
            else if (args[i] == "--ai-session" && !string.IsNullOrWhiteSpace(args[i + 1])) aiSession = args[i + 1];
            else if (args[i] == "--provider-config" && !string.IsNullOrWhiteSpace(args[i + 1])) providerConfig = args[i + 1];
            else if (args[i] == "--product-knowledge" && !string.IsNullOrWhiteSpace(args[i + 1])) productKnowledge = args[i + 1];
            else if (args[i] == "--knowledge-items" && !string.IsNullOrWhiteSpace(args[i + 1])) knowledgeItems = args[i + 1];
            else if (args[i] == "--intent-provider-config" && !string.IsNullOrWhiteSpace(args[i + 1])) intentProviderConfig = args[i + 1];
            else if (args[i] == "--max-run-mb" && double.TryParse(args[i + 1], out var runLimit) && double.IsFinite(runLimit) && runLimit > 0) maxRunMb = runLimit;
            else if (args[i] == "--max-state-mb" && double.TryParse(args[i + 1], out var stateLimit) && double.IsFinite(stateLimit) && stateLimit > 0) maxStateMb = stateLimit;
            else { Console.Error.WriteLine("Invalid shadow option/value."); return 2; }
            i++;
        }
        operation = "Shadow";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var summary = await new QnocrShadowRunner().RunAsync(new(frames, interval, identity, aiShadow, minutes, stateDirectory, aiSession, providerConfig, maxRunMb, maxStateMb,
                productKnowledge, knowledgeItems, intentProviderConfig), cancellation.Token);
            return summary.Status is "Completed" or "Cancelled" or "StoppedByRequest" ? 0 : 3;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    if (args.Length > 0 && args[0] == "--adapt")
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length < 2 || args.Length % 2 != 0)
        {
            Console.Error.WriteLine("Usage: dotnet run -- --adapt buyer_messages.json [--seller-id ID] [--buyer-id ID]");
            return 2;
        }
        for (var i = 2; i < args.Length; i += 2)
        {
            if (args[i] is not ("--seller-id" or "--buyer-id") || !ids.TryAdd(args[i], args[i + 1]) ||
                args[i + 1].Length > 0 && string.IsNullOrWhiteSpace(args[i + 1]))
            {
                Console.Error.WriteLine("Invalid or duplicate identity option. Use --seller-id / --buyer-id, or omit unknown IDs.");
                return 2;
            }
        }
        operation = "Message adapter";
        var batch = JsonSerializer.Deserialize<BuyerMessageBatch>(File.ReadAllText(args[1]))
            ?? throw new InvalidDataException("Message batch JSON is null.");
        var count = await SaveEnvelopes(batch, ids.GetValueOrDefault("--seller-id", ""), ids.GetValueOrDefault("--buyer-id", ""));
        return count > 0 ? 0 : 3;
    }

    if (args.Length > 0 && args[0] == "--messages")
    {
        if (!(args.Length == 2 || args.Length == 4 && args[2] == "--product-id" && ProductIdExtractor.IsValidProductId(args[3])))
        {
            Console.Error.WriteLine("Usage: dotnet run -- --messages chat_region.png [--product-id digits]");
            return 2;
        }
        operation = "Buyer message structuring";
        var ocr = RunOcrAndSave(args[1]);
        var batch = SaveMessages(ocr, args.Length == 4 ? args[3] : "", "ExplicitCallerContextNotVerifiedMessageAssociation");
        operation = "Message adapter";
        await SaveEnvelopes(batch);
        return batch.Messages.Count > 0 ? 0 : 3;
    }

    if (args.Length > 0 && args[0] == "--product")
    {
        if (!(args.Length == 2 || args.Length == 4 && args[2] == "--profiles"))
        {
            Console.Error.WriteLine("Usage: dotnet run -- --product image.png [--profiles catalog.json]");
            return 2;
        }
        operation = "Product context";
        return RunProduct(args[1], args.Length == 4 ? args[3] : null);
    }

    if (args.Length == 1 && args[0] == "--uia-geometry")
    {
        operation = "UIA geometry";
        var target = new QianniuWindowCapture().FindWindows(Console.WriteLine).First(w =>
            w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal));
        var geometry = await new UiaGeometryProbe().ReadAsync(target, target.WindowBounds.Size);
        Directory.CreateDirectory("Output/Layout");
        var json = JsonSerializer.Serialize(geometry, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("Output/Layout/uia_geometry.json", json);
        Console.WriteLine(json);
        return 0;
    }

    if (args.Length == 3 && args[0] == "--compare")
    {
        operation = "Change detection";
        new ChatRegionChangePipeline().Process(args[1], args[2], Path.Combine("Output", "Changes"), RunOcr, Console.WriteLine);
        return 0;
    }

    if (args.Length > 0 && args[0] == "--watch")
    {
        int? maximumFrames = null;
        if (args.Length == 3 && args[1] == "--frames" && int.TryParse(args[2], out var count) && count > 0)
            maximumFrames = count;
        else if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: dotnet run -- --watch [--frames positive-count]");
            return 2;
        }
        operation = "Watch";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { await new ChatRegionMonitor().RunAsync(maximumFrames, RunOcr, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
        return 0;
    }

    if (args.Length == 2 && args[0] == "--ocr")
    {
        operation = "OCR";
        RunOcr(args[1]);
        return 0;
    }

    if (args.Length == 2 && args[0] == "--extract")
    {
        operation = "Region extraction";
        await ExtractRegions(args[1]);
        return 0;
    }

    nint? requestedHandle = null;
    if (args.Length == 2 && args[0] == "--hwnd" &&
        long.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle))
        requestedHandle = (nint)handle;
    else if (args.Length != 0 && !(args.Length == 1 && args[0] is "--list" or "--layout"))
    {
        Console.Error.WriteLine("Usage: dotnet run [-- --shadow [--frames N] [--interval-ms N] [--identity mapping.json] | -- --adapt buyer_messages.json [--seller-id ID] [--buyer-id ID] | -- --list | -- --layout | -- --uia-geometry | -- --hwnd 0xHEX | -- --extract screenshot.png | -- --ocr image.png | -- --messages chat_region.png [--product-id digits] | -- --product image.png [--profiles catalog.json] | -- --compare previous.png current.png | -- --watch [--frames N]]");
        return 2;
    }

    Console.WriteLine("Searching Qianniu...\n");
    var capture = new QianniuWindowCapture();
    var windows = capture.FindWindows(Console.WriteLine);
    foreach (var window in windows.Where(w => w.IsVisible || args.Contains("--list")))
    {
        var bounds = window.WindowBounds;
        Console.WriteLine($"\nFound:\nProcess: {window.ProcessName}\nPID: {window.Pid}\n" +
            $"MainWindowHandle: 0x{window.MainWindowHandle:X}\nHWND: 0x{window.Hwnd:X}\n" +
            $"WindowTitle: {window.WindowTitle}\nBounds: {bounds.Width}x{bounds.Height} (X={bounds.X}, Y={bounds.Y})\n" +
            $"Visible: {window.IsVisible} | Minimized: {window.IsMinimized} | Cloaked: {window.IsCloaked}");
    }
    if (args.Contains("--list")) return 0;
    var selected = windows.FirstOrDefault(w =>
        w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowBounds.Width > 0 && w.WindowBounds.Height > 0 &&
        w.WindowTitle.Contains("接待中心", StringComparison.Ordinal) &&
        (requestedHandle is null || w.Hwnd == requestedHandle));
    if (selected is null)
    {
        Console.Error.WriteLine("\nCapture Failed: No visible, non-minimized Qianniu reception center found. Show the chat window on this desktop and retry.");
        return 1;
    }
    Console.WriteLine($"\nSelected HWND: 0x{selected.Hwnd:X} (prefer reception center, then largest eligible window; --hwnd overrides)");
    Console.WriteLine($"Method: {QianniuWindowCapture.Method}");
    var result = await capture.CaptureAsync(selected, Path.Combine(Environment.CurrentDirectory, "Output"));
    Console.WriteLine(result.Success ? "\nCapture Success" : "\nCapture Failed");
    if (result.SavedPath is not null)
        Console.WriteLine($"\nSaved:\n{Path.GetRelativePath(Environment.CurrentDirectory, result.SavedPath)}\nAbsolute path: {result.SavedPath}");
    Console.WriteLine(result.Message);
    if (result.SavedPath is not null)
        Console.WriteLine($"Near-black samples: {result.NearBlackRatio:P2}\nUnpainted samples: {result.UnpaintedRatio:P2}");
    Console.WriteLine($"CEF: {result.CefContentStatus}\nChat: {result.ChatContentStatus}");
    if (!result.Success || result.SavedPath is null) return 1;
    operation = "Region extraction";
    var regions = await ExtractRegions(result.SavedPath, selected);
    if (args.Contains("--layout")) return 0;
    operation = "OCR";
    var chatOcr = RunOcrAndSave(regions.Single(path => Path.GetFileName(path) == "chat_region.png"));
    operation = "Product context";
    var productContext = RunProductAndSave(regions.Single(path => Path.GetFileName(path) == "product_region.png"));
    operation = "Buyer message structuring";
    var messages = SaveMessages(chatOcr, productContext.ProductId ?? "", "SameCaptureVisibleProductNotVerifiedMessageAssociation");
    operation = "Message adapter";
    await SaveEnvelopes(messages);
    // Product context is optional for a valid buyer message; unresolved IDs remain empty.
    return messages.Messages.Count > 0 ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{operation} Failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task<IReadOnlyList<string>> ExtractRegions(string screenshotPath, QianniuWindow? window = null)
{
    Console.WriteLine($"\nRegion source: {Path.GetFullPath(screenshotPath)}");
    var resolved = await new QianniuLayoutResolver().ResolveAsync(screenshotPath, window);
    QianniuLayoutResolver.SaveDiagnostics(resolved, screenshotPath, Console.WriteLine);
    foreach (var (fileName, bounds) in resolved.ToPixelLayout().GetRegions())
        Console.WriteLine($"{fileName}: X={bounds.X}, Y={bounds.Y}, Width={bounds.Width}, Height={bounds.Height}");
    var paths = new QianniuRegionExtractor().Extract(screenshotPath,
        Path.Combine(Environment.CurrentDirectory, "Output", "Regions"), resolved.ToPixelLayout());
    Console.WriteLine("Region Extraction Success");
    foreach (var path in paths) Console.WriteLine($"Saved: {path}");
    return paths;
}

static void RunOcr(string imagePath)
{
    RunOcrAndSave(imagePath);
}

static OcrResult RunOcrAndSave(string imagePath)
{
    Console.WriteLine($"\nOCR Input:\n{Path.GetFullPath(imagePath)}");
    var result = new PaddleOcrService().Recognize(imagePath);
    Console.WriteLine($"Model: {result.Model}\n\nResult:");
    foreach (var line in result.Lines)
        Console.WriteLine($"\nLine {line.Number}:\n{line.Text}\nLine Confidence: {(line.Confidence is { } score ? score.ToString("F4", CultureInfo.InvariantCulture) : "N/A")}");
    if (result.Lines.Count == 0) Console.WriteLine("No text detected.");
    Console.WriteLine($"\nConfidence:\n{(result.Confidence is { } confidence ? confidence.ToString("F4", CultureInfo.InvariantCulture) : "N/A")}");
    Console.WriteLine("Confidence is the mean recognition score, not measured accuracy.");
    Console.WriteLine($"Discarded empty detections: {result.DiscardedEmptyRegions}");
    Console.WriteLine($"Model initialization: {result.InitializationMilliseconds:F1} ms\nOCR inference: {result.RecognitionMilliseconds:F1} ms\nOCR total (decode + initialization + inference + mapping): {result.TotalMilliseconds:F1} ms");
    Console.WriteLine($"Working set before/after: {result.WorkingSetBeforeBytes / 1048576d:F1} / {result.WorkingSetAfterBytes / 1048576d:F1} MiB\nProcess lifetime peak working set: {result.ProcessPeakWorkingSetBytes / 1048576d:F1} MiB");
    var outputDirectory = Path.Combine(Environment.CurrentDirectory, "Output", "OCR");
    Directory.CreateDirectory(outputDirectory);
    var prefix = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(imagePath)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
    File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(result, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }), new UTF8Encoding(false));
    File.WriteAllText(prefix + ".txt", result.Text, new UTF8Encoding(false));
    Console.WriteLine($"OCR Success\nSaved: {prefix}.json\nSaved: {prefix}.txt");
    return result;
}

static int RunProduct(string imagePath, string? catalogPath = null)
{
    return RunProductAndSave(imagePath, catalogPath).Status == ProductContextStatus.Matched ? 0 : 3;
}

static ProductContext RunProductAndSave(string imagePath, string? catalogPath = null)
{
    catalogPath ??= Path.Combine(AppContext.BaseDirectory, "Product", "product-profiles.sample.json");
    var resolver = new ProductContextResolver(catalogPath);
    Console.WriteLine($"\nProduct catalog: {resolver.CatalogPath}\nTest catalog: {resolver.IsTestCatalog}");
    var ocr = RunOcrAndSave(imagePath);
    var context = resolver.Resolve(ocr);
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var json = JsonSerializer.Serialize(context, options);
    Console.WriteLine($"\nProduct Context:\n{json}");
    Directory.CreateDirectory("Output/Product");
    var path = Path.GetFullPath(Path.Combine("Output", "Product", $"product_context_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json"));
    File.WriteAllText(path, JsonSerializer.Serialize(new { ImagePath = Path.GetFullPath(imagePath), Context = context, Ocr = ocr }, options), new UTF8Encoding(false));
    Console.WriteLine($"Product context saved: {path}");
    return context;
}

static BuyerMessageBatch SaveMessages(OcrResult ocr, string productId, string productBinding)
{
    var batch = new BuyerMessageStructurer().Structure(ocr, productId, productBinding);
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    Directory.CreateDirectory("Output/Messages");
    var path = Path.GetFullPath(Path.Combine("Output", "Messages", $"buyer_messages_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json"));
    File.WriteAllText(path, JsonSerializer.Serialize(batch, options), new UTF8Encoding(false));
    Console.WriteLine($"\nBuyer Messages:\n{JsonSerializer.Serialize(batch.Messages, options)}\nReview items: {batch.ReviewItems.Count}\nMessage result saved: {path}");
    return batch;
}

static async Task<int> SaveEnvelopes(BuyerMessageBatch batch, string sellerId = "", string buyerId = "")
{
    IMessageSource source = new OcrMessageSource(_ => Task.FromResult(batch), sellerId, buyerId);
    var count = 0;
    var runId = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
    await foreach (var envelope in source.ReadAsync())
    {
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory("Output/Adapter");
        var path = Path.GetFullPath(Path.Combine("Output", "Adapter", $"message_envelope_{runId}_{++count:D3}.json"));
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path);
        Console.WriteLine($"\nMessage Envelope:\n{json}\nEnvelope saved: {path}");
    }
    Console.WriteLine($"Adapter emitted {count} envelope(s). No external consumer invoked.");
    return count;
}
