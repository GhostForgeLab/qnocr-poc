using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using QnocrPoc.Models;

namespace QnocrPoc.Capture;

public sealed class QianniuWindowCapture
{
    public const string Method = "Win32 PrintWindow (PW_RENDERFULLCONTENT = 0x2)";
    private static readonly string[] ProcessNames = ["AliWorkbench", "AliRender"];

    public IReadOnlyList<QianniuWindow> FindWindows(Action<string> log)
    {
        var processes = new Dictionary<int, (string Name, nint MainHandle)>();
        foreach (var name in ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    processes[process.Id] = (process.ProcessName + ".exe", process.MainWindowHandle);
                    log($"Process: {process.ProcessName}.exe | PID: {process.Id} | MainWindowHandle: 0x{process.MainWindowHandle:X}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    log($"Process unavailable: {name}: {ex.Message}");
                }
            }
        }

        var windows = new List<QianniuWindow>();
        // CEF hosts may expose their usable top-level windows without a Process.MainWindowHandle.
        if (!EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processes.TryGetValue((int)pid, out var process)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            var hr = DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int));
            windows.Add(new(process.Name, (int)pid, process.MainHandle, hwnd, title.ToString(),
                Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                IsWindowVisible(hwnd), IsIconic(hwnd), hr == 0 && cloaked != 0));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed.");

        return windows.OrderByDescending(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked)
            .ThenByDescending(w => w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
            .ThenByDescending(w => (long)w.WindowBounds.Width * w.WindowBounds.Height).ToList();
    }

    public async Task<CaptureResult> CaptureAsync(QianniuWindow window, string outputDirectory)
    {
        // PrintWindow is synchronous and can hang in a foreign process. Its bitmap stays
        // owned by the background worker; the console process exits on timeout.
        try
        {
            return await Task.Run(() => Capture(window, outputDirectory))
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            return new(false, null, Method, "PrintWindow timed out after 15 seconds. No verified capture.", TimedOut: true);
        }
    }

    private static CaptureResult Capture(QianniuWindow window, string outputDirectory)
    {
        if (!IsWindow(window.Hwnd)) return new(false, null, Method, "The window has closed.");
        GetWindowThreadProcessId(window.Hwnd, out var pid);
        if (pid != window.Pid) return new(false, null, Method, "The window owner changed; please retry.");
        if (!IsWindowVisible(window.Hwnd) || IsIconic(window.Hwnd))
            return new(false, null, Method, "Please show/restore the Qianniu window and retry.");
        if (!GetWindowRect(window.Hwnd, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            return new(false, null, Method, $"Invalid capture bounds: {width}x{height}.");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // An untouched buffer must not be mistaken for a successful capture.
            graphics.Clear(Color.Magenta);
            var dc = graphics.GetHdc();
            bool rendered;
            try { rendered = PrintWindow(window.Hwnd, dc, 2); }
            finally { graphics.ReleaseHdc(dc); }
            if (!rendered) return new(false, null, Method, "PrintWindow returned false (CEF/GPU rendering is not guaranteed).");
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.GetFullPath(Path.Combine(outputDirectory, $"qianniu_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
        // Never overwrite a prior sample taken within the same second.
        if (File.Exists(path)) path = Path.Combine(outputDirectory, $"qianniu_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        path = Path.GetFullPath(path);
        bitmap.Save(path, ImageFormat.Png);

        var black = 0;
        var unpainted = 0;
        var count = 0;
        var minLuma = 255;
        var maxLuma = 0;
        for (var y = 0; y < height; y += Math.Max(1, height / 200))
        for (var x = 0; x < width; x += Math.Max(1, width / 200))
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.R < 12 && pixel.G < 12 && pixel.B < 12) black++;
            if (pixel.R == 255 && pixel.G == 0 && pixel.B == 255) unpainted++;
            var luma = (pixel.R + pixel.G + pixel.B) / 3;
            minLuma = Math.Min(minLuma, luma);
            maxLuma = Math.Max(maxLuma, luma);
            count++;
        }
        var blackRatio = (double)black / count;
        var unpaintedRatio = (double)unpainted / count;
        var suspect = blackRatio > 0.98 || unpaintedRatio > 0.05 || maxLuma - minLuma < 4;
        return new(!suspect, path, Method,
            suspect ? "Suspected blank/black/unpainted frame; diagnostic PNG saved."
                : "PNG saved; pixel sanity check passed. This does not prove CEF/chat content is complete.",
            blackRatio, unpaintedRatio);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
