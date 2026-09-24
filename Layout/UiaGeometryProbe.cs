using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using QnocrPoc.Models;

namespace QnocrPoc.Layout;

/// <summary>Reads only ControlType, BoundingRectangle and IsOffscreen. Never requests names, values or patterns.</summary>
public sealed class UiaGeometryProbe
{
    private static readonly object Gate = new();
    private static Task<UiaGeometrySnapshot>? _inFlight;

    public async Task<UiaGeometrySnapshot> ReadAsync(QianniuWindow window, Size screenshotSize)
    {
        Task<UiaGeometrySnapshot> pending;
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false }) return Unavailable("UIA previous probe still busy; skipped");
            var completion = new TaskCompletionSource<UiaGeometrySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = _inFlight = completion.Task;
            var thread = new Thread(() =>
            {
                try { completion.TrySetResult(ReadCore(window, screenshotSize)); }
                catch (Exception ex) { completion.TrySetResult(Unavailable($"UIA unavailable ({ex.GetType().Name})")); }
            }) { IsBackground = true, Name = "Qianniu geometry-only UIA" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(1800)); }
        catch (TimeoutException) { return Unavailable("UIA timed out after 1800 ms; fallback (one bounded worker)"); }
    }

    public static UiaGeometrySnapshot Unavailable(string reason) => new(false, reason, Rectangle.Empty, []);

    private static UiaGeometrySnapshot ReadCore(QianniuWindow window, Size screenshotSize)
    {
        GetWindowThreadProcessId(window.Hwnd, out var pid);
        if (pid != window.Pid || !GetWindowRect(window.Hwnd, out var native)) return Unavailable("UIA HWND owner unavailable/changed");
        var windowBounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        if (windowBounds.Size != screenshotSize) return Unavailable("UIA window size differs from captured image; skipped");

        var timer = Stopwatch.StartNew();
        var root = AutomationElement.FromHandle(window.Hwnd);
        var walker = TreeWalker.RawViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth, int Id)>();
        queue.Enqueue((root, 0, 0));
        var nodes = new List<UiaLayoutNode>();
        var visited = 0;
        var complete = true;
        var stopReason = "complete permitted-layout traversal";
        while (queue.Count > 0)
        {
            if (visited >= 512 || timer.ElapsedMilliseconds > 1200) { complete = false; stopReason = "node/time budget reached"; break; }
            var (parent, depth, parentId) = queue.Dequeue();
            if (depth >= 10) { complete = false; stopReason = "depth budget reached"; continue; }
            var child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                if (visited >= 512 || timer.ElapsedMilliseconds > 1200) { complete = false; stopReason = "node/time budget reached"; break; }
                var id = ++visited;
                var type = child.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                if (type == ControlType.Pane || type == ControlType.Document || type == ControlType.Window || type == ControlType.Group)
                {
                    var offscreen = child.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty);
                    var value = child.GetCurrentPropertyValue(AutomationElement.BoundingRectangleProperty);
                    if (offscreen is false && value is System.Windows.Rect bounds && !bounds.IsEmpty &&
                        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) &&
                        bounds.Left >= windowBounds.Left - .5 && bounds.Top >= windowBounds.Top - .5 &&
                        bounds.Right <= windowBounds.Right + .5 && bounds.Bottom <= windowBounds.Bottom + .5)
                    {
                        var rectangle = Rectangle.FromLTRB((int)Math.Round(bounds.Left - windowBounds.Left),
                            (int)Math.Round(bounds.Top - windowBounds.Top), (int)Math.Round(bounds.Right - windowBounds.Left),
                            (int)Math.Round(bounds.Bottom - windowBounds.Top));
                        if (rectangle.Width > 0 && rectangle.Height > 0 && new Rectangle(Point.Empty, screenshotSize).Contains(rectangle))
                            nodes.Add(new(id, parentId, depth + 1, type.ProgrammaticName, rectangle));
                    }
                    queue.Enqueue((child, depth + 1, id));
                }
                // Non-layout controls (including Text/Edit) are neither inspected further nor traversed.
                child = walker.GetNextSibling(child);
            }
        }
        GetWindowThreadProcessId(window.Hwnd, out var afterPid);
        if (afterPid != window.Pid || !GetWindowRect(window.Hwnd, out var after) || native != after)
            return Unavailable("UIA window moved/resized while probing; skipped");
        return new(complete, $"UIA geometry-only: visited={visited}, layout nodes={nodes.Count}, complete={complete}; {stopReason}", windowBounds, nodes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
}
