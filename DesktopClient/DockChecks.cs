using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QnocrPoc.Models;

namespace QNAgent;

internal static class DockChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        var work = new Rect(0, 0, 1920, 1040);
        var right = WindowAttachment.Place(new Rect(100, 100, 900, 700), work, 1, false);
        Check(right.Left == 1006 && right.Width == 390, "panel attaches to right with a small gap");
        var left = WindowAttachment.Place(new Rect(800, 100, 1000, 700), work, 1, false);
        Check(left.Right == 794, "insufficient right space chooses left side");
        Check(work.Contains(WindowAttachment.Place(work, work, 1, false)), "maximized host keeps panel within screen");
        var negativeMonitor = new Rect(-2560, -200, 2560, 1440);
        Check(negativeMonitor.Contains(WindowAttachment.Place(new Rect(-2500, -100, 1800, 1000), negativeMonitor, 1.5, false)), "mixed DPI and negative monitor coordinates remain on screen");
        Check(WindowAttachment.Place(work, work, 2, true).Height == 112, "collapsed panel respects display scaling");
        var focus = new ConversationFocus();
        InboxMessage Message(string shop, string buyer, string id, int minute) => new("session-" + shop + buyer, id, shop, buyer,
            "受控测试消息", new DateTime(2026, 10, 23, 10, minute, 0), "", "memory:test", true);
        var a = Message("shop1", "buyer-a", "a", 1); var b = Message("shop1", "buyer-b", "b", 2); var otherShop = Message("shop2", "buyer-a", "c", 3);
        focus.Observe(new("shop1", "buyer-a", ""));
        Check(focus.Messages(new[] { a, b, otherShop }).SequenceEqual(new[] { a }), "panel isolates current buyer and shop");
        focus.Remember(a, "A 的编辑草稿"); var revision = focus.Revision;
        focus.Observe(new("shop1", null, "pending"));
        Check(focus.Messages(new[] { a, b }).Length == 0 && !focus.Contains(a), "unconfirmed frame immediately removes old buyer actions");
        Check(focus.Revision != revision, "in-flight AI reply is invalidated by a frame/buyer change");
        focus.Observe(new("shop1", "buyer-b", ""));
        Check(focus.Draft(b) is null, "draft never leaks to another buyer");
        focus.Observe(new("shop1", "buyer-a", ""));
        Check(focus.Draft(a) == "A 的编辑草稿", "returning to buyer retains edited draft");

        // Real Win32 movement events, using only our invisible synthetic windows.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previewRoot = Path.GetFullPath(Path.Combine("Output", "DockChecks", Guid.NewGuid().ToString("N")));
        var store = new ClientStore(previewRoot);
        var settingsWindow = new MainWindow(store);
        var binding = new WindowBinding(Guid.NewGuid().ToString("N"), "fixture", "shop1", "受控窗口");
        var state = new WindowBindingRegistry(store).OpenStore(binding);
        state.SaveMessages(new[] { a with { Buyer = "测试买家", Text = "这个杯子可以装热水吗？容量是多少？", WindowScopeId = binding.ScopeId } });
        var reception = new ReceptionSession(store, state, binding,
            new QianniuWindow("controlled", Environment.ProcessId, 0, 0, "受控窗口", new System.Drawing.Rectangle(0, 0, 800, 640), true, false, false),
            settingsWindow.Resources, _ => { }, _ => { });
        object Field(string name) => typeof(ReceptionSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reception)!;
        reception.ObserveConversation(new("shop1", "测试买家", "当前千牛会话 · 回复需人工确认"));
        ((TextBox)Field("_reply")).Text = "这款的耐热范围和容量还需要核对商品资料，我确认后马上回复您。";
        ((TextBlock)Field("_replyState")).Text = "建议已生成 · 请核对后复制";
        var previewWindow = (ConversationWindow)Field("_conversationWindow");
        var visual = (FrameworkElement)previewWindow.Content;
        visual.Measure(new Size(390, 720)); visual.Arrange(new Rect(0, 0, 390, 720)); visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(390, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(previewRoot, "conversation-preview.png"))) encoder.Save(output);
        Console.WriteLine("Sanitized UI preview: " + Path.Combine(previewRoot, "conversation-preview.png"));
        Check(((TextBlock)Field("_question")).Text.Contains("热水"), "actual panel renders only selected buyer question");
        using (var sticker = new System.Drawing.Bitmap(80, 80))
        using (var graphics = System.Drawing.Graphics.FromImage(sticker))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.FillEllipse(System.Drawing.Brushes.Gold, 5, 5, 70, 70);
            graphics.FillEllipse(System.Drawing.Brushes.Black, 24, 24, 6, 8);
            graphics.FillEllipse(System.Drawing.Brushes.Black, 50, 24, 6, 8);
            graphics.DrawArc(System.Drawing.Pens.Black, 24, 30, 32, 26, 0, 180);
            using var png = new MemoryStream(); sticker.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            var selected = (InboxMessage)Field("_selected");
            typeof(ReceptionSession).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(reception, selected with { ContentKind = "Unrecognized" });
            ((System.Collections.Generic.Dictionary<string, byte[]>)Field("_messagePreviews"))[selected.MessageId] = png.ToArray();
            ((TextBlock)Field("_question")).Text = "[消息内容未识别，请查看原图]";
            ((TextBox)Field("_reply")).Text = "收到您的消息了，方便再用文字说明一下您想咨询的问题吗？";
            typeof(ReceptionSession).GetMethod("ShowMessagePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reception, null);
            Check(((System.Windows.Controls.Image)Field("_messageImage")).Visibility == Visibility.Visible &&
                ((TextBlock)Field("_visualNote")).Text.Contains("尚未识别"), "visual message displays pixels and uncertainty together");
            visual.Measure(new Size(390, 720)); visual.Arrange(new Rect(0, 0, 390, 720)); visual.UpdateLayout();
            var thumbnail = new RenderTargetBitmap(390, 720, 96, 96, PixelFormats.Pbgra32); thumbnail.Render(visual);
            var screenshot = new PngBitmapEncoder(); screenshot.Frames.Add(BitmapFrame.Create(thumbnail));
            using (var output = File.Create(Path.Combine(previewRoot, "visual-message-preview.png"))) screenshot.Save(output);
            Console.WriteLine("Sanitized visual-message preview: " + Path.Combine(previewRoot, "visual-message-preview.png"));
        }
        reception.StopAsync().GetAwaiter().GetResult(); settingsWindow.Close();
        var host = new Window { Title = "QN Agent synthetic docking host", ShowActivated = false, ShowInTaskbar = false,
            Opacity = 0, Width = 800, Height = 640, Left = 40, Top = 60, WindowStartupLocation = WindowStartupLocation.Manual };
        var panel = new ConversationWindow(new TextBlock { Text = "受控会话面板" }, () => { }, () => { }) { Opacity = 0 };
        var hostB = new Window { Title = "QN Agent synthetic host B", ShowActivated = false, ShowInTaskbar = false,
            Opacity = 0, Width = 700, Height = 600, Left = 100, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual };
        var panelB = new ConversationWindow(new TextBlock { Text = "独立面板 B" }, () => { }, () => { }) { Opacity = 0 };
        bool lost = false;
        try
        {
            host.Show(); Pump();
            var hwnd = new WindowInteropHelper(host).Handle;
            var original = Bounds(hwnd); var foreground = GetForegroundWindow();
            var target = new QianniuWindow("controlled", Environment.ProcessId, hwnd, hwnd, host.Title,
                new System.Drawing.Rectangle((int)original.X, (int)original.Y, (int)original.Width, (int)original.Height), true, false, false);
            panel.Attach(target, () => lost = true); Pump();
            var phwnd = new WindowInteropHelper(panel).Handle;
            Check(Bounds(hwnd) == original, "attaching never moves or resizes the host window");
            Check(GetForegroundWindow() == foreground, "attaching does not steal keyboard focus");
            var oldPanel = Bounds(phwnd);
            hostB.Show(); Pump();
            var bhwnd = new WindowInteropHelper(hostB).Handle;
            panelB.Attach(target with { Hwnd = bhwnd, MainWindowHandle = bhwnd, WindowTitle = hostB.Title }, () => { }); Pump();
            var bpanel = new WindowInteropHelper(panelB).Handle; var oldB = Bounds(bpanel);
            host.Left += 50; host.Top += 20; Pump();
            var moved = Bounds(phwnd);
            Check(moved.Left != oldPanel.Left && moved.Top != oldPanel.Top, "native movement event moves the attached panel");
            Check(Bounds(bpanel) == oldB, "moving host A does not move host B's panel");
            host.WindowState = WindowState.Minimized; Pump();
            Check(!IsWindowVisible(phwnd), "minimizing host hides the panel");
            Check(IsWindowVisible(bpanel), "minimizing host A does not hide host B's panel");
            host.WindowState = WindowState.Normal; Pump();
            Check(IsWindowVisible(phwnd), "restoring host restores the panel");
            panel.Detach(); host.Left += 30; Pump();
            Check(!panel.IsVisible, "detach stops following and hides panel");
            panel.Attach(target, () => lost = true); host.Close(); Pump();
            Check(lost || !IsWindowVisible(phwnd), "closing host hides panel or reports lost target");
            Check(IsWindowVisible(bpanel), "closing host A leaves host B's panel visible");
        }
        finally { panel.Exit(); panelB.Exit(); host.Close(); hostB.Close(); app.Shutdown(); }
        Console.WriteLine($"Passed {passed} docking and current-conversation checks."); return 0;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static Rect Bounds(nint hwnd)
    { GetWindowRect(hwnd, out var r); return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
