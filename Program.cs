using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using QnocrPoc.Capture;
using QnocrPoc.Models;
using QnocrPoc.OCR;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("[QN OCR POC V0.3]");
Console.WriteLine($"Time: {DateTimeOffset.Now:O}");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var operation = "Capture";

try
{
    if (args.Length == 2 && args[0] == "--ocr")
    {
        operation = "OCR";
        RunOcr(args[1]);
        return 0;
    }

    if (args.Length == 2 && args[0] == "--extract")
    {
        operation = "Region extraction";
        ExtractRegions(args[1]);
        return 0;
    }

    nint? requestedHandle = null;
    if (args.Length == 2 && args[0] == "--hwnd" &&
        long.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle))
        requestedHandle = (nint)handle;
    else if (args.Length != 0 && !(args.Length == 1 && args[0] == "--list"))
    {
        Console.Error.WriteLine("Usage: dotnet run [-- --list | -- --hwnd 0xHEX | -- --extract screenshot.png | -- --ocr image.png]");
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
        (requestedHandle is null || w.Hwnd == requestedHandle));
    if (selected is null)
    {
        Console.Error.WriteLine("\nCapture Failed: No visible, non-minimized Qianniu window found. Show the chat window on this desktop and retry.");
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
    var regions = ExtractRegions(result.SavedPath);
    operation = "OCR";
    RunOcr(regions.Single(path => Path.GetFileName(path) == "chat_region.png"));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{operation} Failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static IReadOnlyList<string> ExtractRegions(string screenshotPath)
{
    var layout = new QianniuLayout();
    Console.WriteLine($"\nRegion source: {Path.GetFullPath(screenshotPath)}");
    Console.WriteLine("Layout: fixed 1638x1100 reception center; manually calibrated pixel coordinates, no scaling.");
    foreach (var (fileName, bounds) in layout.GetRegions())
        Console.WriteLine($"{fileName}: X={bounds.X}, Y={bounds.Y}, Width={bounds.Width}, Height={bounds.Height}");
    var paths = new QianniuRegionExtractor().Extract(screenshotPath,
        Path.Combine(Environment.CurrentDirectory, "Output", "Regions"), layout);
    Console.WriteLine("Region Extraction Success");
    foreach (var path in paths) Console.WriteLine($"Saved: {path}");
    return paths;
}

static void RunOcr(string imagePath)
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
}
