using System.Globalization;
using System.Text;
using QnocrPoc.Capture;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("[QN OCR POC]");
Console.WriteLine($"Time: {DateTimeOffset.Now:O}");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine("Searching Qianniu...\n");

try
{
    nint? requestedHandle = null;
    if (args.Length == 2 && args[0] == "--hwnd" &&
        long.TryParse(args[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle))
        requestedHandle = (nint)handle;
    else if (args.Length != 0 && !(args.Length == 1 && args[0] == "--list"))
    {
        Console.Error.WriteLine("Usage: dotnet run [-- --list | -- --hwnd 0xHEX]");
        return 2;
    }

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
    return result.Success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Capture Failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
