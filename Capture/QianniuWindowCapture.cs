using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
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

    // A connected window is already selected: validate that HWND directly instead of scanning
    // every AliRender process again for each frame of every connected window.
    public QianniuWindow? ReadWindow(nint hwnd, int expectedPid, string expectedTitle)
    {
        if (!IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != expectedPid || !GetWindowRect(hwnd, out var rect)) return null;
        try
        {
            using var process = Process.GetProcessById(expectedPid);
            if (!ProcessNames.Contains(process.ProcessName)) return null;
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (!string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal) || !expectedTitle.Contains("接待中心", StringComparison.Ordinal)) return null;
            var hr = DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int));
            return new(process.ProcessName + ".exe", expectedPid, hwnd, hwnd, expectedTitle,
                Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), IsWindowVisible(hwnd), IsIconic(hwnd), hr == 0 && cloaked != 0);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static CaptureResult Capture(QianniuWindow window, string outputDirectory)
        => CaptureCore(window, outputDirectory, out _);

    // Synchronous native call: callers must run this in an owned, timeout-bounded process.
    public static (CaptureResult Result, byte[]? Png) CaptureMemory(QianniuWindow window)
        => CaptureMemory(window, null, out _);

    public static (CaptureResult Result, byte[]? Png) CaptureMemory(QianniuWindow window, string? previousFingerprint, out string? fingerprint)
    {
        var result = CaptureCore(window, null, out var png, previousFingerprint, out fingerprint);
        return (result, png);
    }

    private static CaptureResult CaptureCore(QianniuWindow window, string? outputDirectory, out byte[]? png)
        => CaptureCore(window, outputDirectory, out png, null, out _);

    private static CaptureResult CaptureCore(QianniuWindow window, string? outputDirectory, out byte[]? png,
        string? previousFingerprint, out string? fingerprint)
    {
        png = null;
        fingerprint = null;
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

        string? path = null;
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
            path = Path.GetFullPath(Path.Combine(outputDirectory, $"qianniu_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
            if (File.Exists(path)) path = Path.Combine(outputDirectory, $"qianniu_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
            bitmap.Save(path, ImageFormat.Png);
        }

        var pixels = InspectPixels(bitmap, outputDirectory is null);
        var blackRatio = pixels.BlackRatio;
        var unpaintedRatio = pixels.UnpaintedRatio;
        var suspect = pixels.Suspect;
        if (!suspect && outputDirectory is null)
        {
            fingerprint = pixels.Fingerprint;
            if (fingerprint != previousFingerprint)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                png = stream.ToArray();
            }
        }
        return new(!suspect, path, Method,
            suspect ? "Suspected blank/black/unpainted frame."
                : "Pixel sanity check passed. This does not prove CEF/chat content is complete.",
            blackRatio, unpaintedRatio);
    }

    public static (string? Fingerprint, double BlackRatio, double UnpaintedRatio, bool Suspect) InspectPixels(Bitmap bitmap, bool fingerprint = true)
    {
        using var hash = fingerprint ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        hash?.AppendData(BitConverter.GetBytes(bitmap.Width)); hash?.AppendData(BitConverter.GetBytes(bitmap.Height));
        int black = 0, unpainted = 0, count = 0, minLuma = 255, maxLuma = 0;
        int yStep = Math.Max(1, bitmap.Height / 200), xStep = Math.Max(1, bitmap.Width / 200);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y += fingerprint ? 1 : yStep)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                if (y % yStep == 0)
                for (var x = 0; x < bitmap.Width; x += xStep)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (r < 12 && g < 12 && b < 12) black++;
                    if (r == 255 && g == 0 && b == 255) unpainted++;
                    var luma = (r + g + b) / 3;
                    minLuma = Math.Min(minLuma, luma); maxLuma = Math.Max(maxLuma, luma); count++;
                }
                // Format32bppRgb's unused alpha byte is not part of visible content.
                if (hash is not null)
                {
                    for (var x = 3; x < row.Length; x += 4) row[x] = 0;
                    hash.AppendData(row);
                }
            }
            var blackRatio = (double)black / count; var unpaintedRatio = (double)unpainted / count;
            return (hash is null ? null : Convert.ToHexString(hash.GetHashAndReset()), blackRatio, unpaintedRatio,
                blackRatio > .98 || unpaintedRatio > .05 || maxLuma - minLuma < 4);
        }
        finally { bitmap.UnlockBits(data); }
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
