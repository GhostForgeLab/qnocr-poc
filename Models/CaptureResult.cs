using System.Drawing;

namespace QnocrPoc.Models;

public sealed record QianniuWindow(
    string ProcessName, int Pid, nint MainWindowHandle, nint Hwnd,
    string WindowTitle, Rectangle WindowBounds, bool IsVisible, bool IsMinimized, bool IsCloaked);

public sealed record CaptureResult(
    bool Success, string? SavedPath, string Method, string Message,
    double NearBlackRatio = 0, double UnpaintedRatio = 0,
    string CefContentStatus = "Not verified: manual image inspection required",
    string ChatContentStatus = "Not verified: manual image inspection required",
    bool TimedOut = false);
