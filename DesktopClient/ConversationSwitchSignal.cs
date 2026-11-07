using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace QNAgent;

// Installed on the UI thread. Inspect only mouse-up in the selected reception's buyer list;
// no key logging, no input injection, no retained cursor history.
internal sealed class ConversationSwitchSignal : IDisposable
{
    private readonly MouseHook _callback;
    private nint _hook;
    private readonly nint _target;
    private readonly Func<Rectangle?> _buyerList;
    private readonly Action _wake;
    public ConversationSwitchSignal(nint target, Func<Rectangle?> buyerList, Action wake)
    {
        _target = target; _buyerList = buyerList; _wake = wake; _callback = OnMouse;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
    }
    private nint OnMouse(int code, nint message, nint pointer)
    {
        try
        {
            if (code >= 0 && message == 0x0202 && GetForegroundWindow() == _target &&
                _buyerList() is { } list && GetWindowRect(_target, out var window))
            {
                var point = Marshal.PtrToStructure<MouseData>(pointer);
                if (list.Contains(point.X - window.Left, point.Y - window.Top)) _wake();
            }
        }
        catch { /* A failed optional wake-up must never interfere with the user's click. */ }
        return CallNextHookEx(_hook, code, message, pointer);
    }
    public void Dispose() { if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; } }
    private delegate nint MouseHook(int code, nint message, nint pointer);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, MouseHook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
}
