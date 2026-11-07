using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QnocrPoc.Models;

namespace QNAgent;

internal static class IntentCardChecks
{
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    internal static int Run()
    {
        int count = 0; void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var ack = new JevPurpose("ACKNOWLEDGE", new JevChoice("ACKNOWLEDGE", .92, .91));
        var presentation = BuyerIntentDescriptions.Present(ack);
        Check(presentation.Summary.Contains("已了解") && presentation.Guidance.Contains("不重复") && presentation.Confidence.Contains("92"), "card maps JEV purpose to useful intent, probability and guidance");
        Check(BuyerIntentDescriptions.Present(ack with { Judgement = new JevChoice("ACKNOWLEDGE", .40, .30) }).Uncertain, "uncertain purpose is not displayed as a definite buyer intention");
        Check(BuyerIntentDescriptions.Present(new JevPurpose("UNCLEAR", new JevChoice("UNCLEAR", .99, .99))).Confidence == "需确认", "confident unclear judgment still asks for confirmation");
        Check(!BuyerIntentDescriptions.IsSocial(new JevPurpose("ASK_PRICE", new JevChoice("ASK_PRICE", 1, 1))), "price requests cannot become acknowledgement-only replies");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = Path.GetFullPath(Path.Combine("Output", "IntentCardChecks", Guid.NewGuid().ToString("N")));
        var store = new ClientStore(root); var main = new MainWindow(store);
        var registry = new WindowBindingRegistry(store);
        var target = new QianniuWindow("fixture", Environment.ProcessId, 100, 100, "fixture", new System.Drawing.Rectangle(0, 0, 800, 600), true, false, false);
        var binding = registry.GetOrCreate(target, "演示店铺", "fixture");
        var session = new ReceptionSession(store, registry.OpenStore(binding), binding, target, main.Resources, _ => { }, _ => { }, new FixtureJevGateway(), false);
        try
        {
            var message = new InboxMessage("fixture", "m1", "演示店铺", "演示买家", "好的", DateTime.Now, "", "fixture", false) { WindowScopeId = binding.ScopeId };
            session.AcceptMessage(message); session.ObserveConversation(new("演示店铺", "演示买家", "当前会话"));
            var analysis = new JevAssessment(new IntentResult("ACKNOWLEDGEMENT", .91, false, false, true, "Medium", "JEV/fixture"), "SOCIAL_REPLY", "fixture", new JevChoice("SOCIAL_REPLY", .95, .94), ack);
            typeof(ReceptionSession).GetMethod("ShowIntent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object?[] { analysis, null, false });
            Field<TextBox>(session, "_reply").Text = "好的，有需要随时联系。";
            Check(Field<TextBlock>(session, "_intentSummary").Text.Contains("已了解") && Field<TextBlock>(session, "_intentGuidance").Text.Contains("简短回应"), "real WPF intent card shows the current structured analysis");
            Check(ReferenceEquals(main.Icon, AppBranding.WindowIcon) && ReferenceEquals(session.Panel.Icon, AppBranding.WindowIcon), "settings and conversation windows share the dedicated icon");
            using var tray = AppBranding.CreateTrayIcon(); Check(tray.Width == 32, "tray uses the dedicated 32-pixel icon frame");
            using var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "QNAgent.exe"));
            Check(executableIcon is not null, "built executable contains an extractable application icon");
            var visual = (FrameworkElement)session.Panel.Content; visual.Measure(new Size(390, 850)); visual.Arrange(new Rect(0, 0, 390, 850)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(390, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, "intent-card.png"))) encoder.Save(file);
            session.ObserveConversation(new("演示店铺", "另一个买家", "已切换"));
            Check(!Field<TextBlock>(session, "_intentSummary").Text.Contains("已了解") && Field<TextBlock>(session, "_intentConfidence").Text == "", "buyer switch clears the prior buyer's inferred intention");
            Console.WriteLine($"Passed {count} intent-card and icon checks. Preview: {root}"); return 0;
        }
        finally { session.StopAsync().GetAwaiter().GetResult(); main.Close(); app.Shutdown(); }
    }
    internal static async Task<int> LiveAsync(string root)
    {
        var settings = new ClientStore(Path.Combine(root, "Output", "DesktopClient")).Settings();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var jev = new JevGateway();
        string State(string question) => JsonSerializer.Serialize(new
        {
            BuyerQuestion = question,
            Conversation = new[] { new { Role = "buyer", Text = "这个收纳盒是什么材质？" }, new { Role = "seller", Text = "资料注明是纯钛材质。" }, new { Role = "buyer", Text = question } },
            Product = new { Name = "虚构测试收纳盒", Fields = new { Material = "纯钛", CurrentPrice = "128元" } }, ShopPromotions = (object?)null
        });
        var ack = await DesktopReplyPipeline.RunAsync(settings, State("好的"), true, jev, timeout.Token);
        Console.WriteLine($"Live acknowledgement: purpose={ack.Assessment?.Purpose?.Name}, mode={ack.Mode}, reply={ack.Reply}");
        if (ack.Assessment?.Purpose?.Name != "ACKNOWLEDGE" || ack.Mode != "SOCIAL_REPLY" || ack.Reply.Length > 50 ||
            new[] { "纯钛", "尺寸", "128", "哪款", "哪一款" }.Any(ack.Reply.Contains)) throw new Exception("Acknowledgement still solicits a product discussion.");
        var question = await jev.AnalyzeAsync(settings, State("好的，多少钱？"), timeout.Token);
        Console.WriteLine($"Live mixed acknowledgement + question: purpose={question.Purpose?.Name}, intent={question.Intent.IntentName}, mode={question.Mode}");
        if (question.Purpose?.Name != "ASK_PRICE" || question.Mode == "SOCIAL_REPLY") throw new Exception("Real question after acknowledgement was lost.");
        var select = await jev.AnalyzeAsync(settings, State("我要黑色款一个"), timeout.Token);
        Console.WriteLine($"Live explicit selection: purpose={select.Purpose?.Name}");
        if (select.Purpose?.Name != "CONFIRM_SELECTION") throw new Exception("Explicit selection was not distinguished from acknowledgement.");
        Console.WriteLine("Live intent-purpose checks passed using fictional product data."); return 0;
    }
}
