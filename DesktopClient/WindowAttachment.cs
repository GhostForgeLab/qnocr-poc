using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using QnocrPoc.Models;

namespace QNAgent;

// Follows the same design as qnagent/qnbot WndAssist.AttachedPerformance:
// target-window events, low-frequency fallback, no host movement and no foreground stealing.
internal sealed class WindowAttachment : IDisposable
{
    private readonly Window _panel;
    private readonly QianniuWindow _target;
    private readonly Action _lost;
    private readonly WinEvent _callback;
    private readonly List<nint> _hooks = new();
    private readonly DispatcherTimer _coalesce, _fallback;
    private bool _disposed, _collapsed;
    private RECT? _last;
    private nint _handle;
    public WindowAttachment(Window panel, QianniuWindow target, Action lost)
    {
        _panel = panel; _target = target; _lost = lost; _callback = OnEvent;
        _handle = new WindowInteropHelper(panel).EnsureHandle();
        // Native ownership keeps the panel with Qianniu's Z-order, without global Topmost.
        new WindowInteropHelper(panel).Owner = target.Hwnd;
        _coalesce = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _coalesce.Tick += (_, _) => { _coalesce.Stop(); Update(); };
        _fallback = new DispatcherTimer(DispatcherPriority.Background, panel.Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _fallback.Tick += (_, _) => Update();
        Hook(0x8001, 0x8003); // destroy/show/hide
        Hook(0x800B, 0x800B); // location/size
        Hook(0x0016, 0x0017); // minimize start/end
        _fallback.Start(); Update();
    }
    private void Hook(uint first, uint last)
    {
        var hook = SetWinEventHook(first, last, 0, _callback, (uint)_target.Pid, 0, 0);
        if (hook != 0) _hooks.Add(hook);
    }
    private void OnEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time)
    {
        if (_disposed || hwnd != _target.Hwnd || obj != 0 || child != 0) return;
        if (!_coalesce.IsEnabled) _coalesce.Start();
    }
    public void SetCollapsed(bool collapsed) { _collapsed = collapsed; _last = null; Update(); }
    public void Refresh() { _last = null; Update(); }
    private void Update()
    {
        if (_disposed) return;
        GetWindowThreadProcessId(_target.Hwnd, out var pid);
        if (!IsWindow(_target.Hwnd) || pid != _target.Pid)
        { Dispose(); _panel.Hide(); _lost(); return; }
        var cloaked = 0;
        DwmGetWindowAttribute(_target.Hwnd, 14, out cloaked, sizeof(int));
        if (!IsWindowVisible(_target.Hwnd) || IsIconic(_target.Hwnd) || cloaked != 0)
        { if (_panel.IsVisible) _panel.Hide(); _last = null; return; }
        if (!GetWindowRect(_target.Hwnd, out var host)) return;
        var screen = System.Windows.Forms.Screen.FromHandle(_target.Hwnd).WorkingArea;
        var scale = Math.Max(96u, GetDpiForWindow(_target.Hwnd)) / 96d;
        var position = Place(new Rect(host.Left, host.Top, host.Right - host.Left, host.Bottom - host.Top),
            new Rect(screen.X, screen.Y, screen.Width, screen.Height), scale, _collapsed);
        var desired = new RECT { Left = (int)position.X, Top = (int)position.Y, Right = (int)position.Right, Bottom = (int)position.Bottom };
        if (!_panel.IsVisible) _panel.Show(); // ShowActivated=false
        if (_last is not { } last || !last.Equals(desired))
        {
            SetWindowPos(_handle, 0, desired.Left, desired.Top, desired.Right - desired.Left, desired.Bottom - desired.Top,
                0x0004 | 0x0010 | 0x0200); // NOZORDER, NOACTIVATE, NOOWNERZORDER; only OUR HWND
            _last = desired;
        }
    }
    internal static Rect Place(Rect host, Rect work, double scale, bool collapsed)
    {
        var width = Math.Min((collapsed ? 250 : 390) * scale, work.Width);
        var height = Math.Min(collapsed ? 56 * scale : Math.Clamp(host.Height, 480 * scale, 820 * scale), work.Height);
        var gap = 6 * scale;
        var x = host.Right + gap;
        if (x + width > work.Right) x = host.Left - gap - width;
        if (x < work.Left) x = host.Right - width - gap; // maximized: stay inside the right edge
        return new Rect(Math.Clamp(x, work.Left, work.Right - width),
            Math.Clamp(host.Top, work.Top, work.Bottom - height), width, height);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _coalesce.Stop(); _fallback.Stop();
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate void WinEvent(nint hook, uint evt, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
}
