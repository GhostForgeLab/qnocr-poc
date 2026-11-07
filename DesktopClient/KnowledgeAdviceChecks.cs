using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QNAgent;

internal static class KnowledgeAdviceChecks
{
    internal static readonly string Fixture = JsonSerializer.Serialize(new { suggestions = new[]
    {
        new { topic = "return_conditions", category = "售后", priority = "优先", status = "缺失", target = "ReturnPolicy", question = "收到后不合适可以退换吗？", reason = "当前资料未说明退换条件。", suggestedAction = "请补充实际退换条件、处理方式和运费承担。" },
        new { topic = "cleaning_method", category = "使用保养", priority = "一般", status = "缺失", target = "Faqs", question = "平时怎么清洁和保养？", reason = "当前资料未提供清洁方法。", suggestedAction = "请确认可用的清洁方式及需要避免的操作。" }
    } });
    private sealed class FakeClient : IKnowledgeAdviceClient
    {
        public int Calls; public readonly List<string> Contexts = new();
        public Func<Task<string>> Response = () => Task.FromResult(Fixture);
        public Task<string> GenerateAsync(ClientSettings settings, string context, CancellationToken token)
        { Calls++; Contexts.Add(context); return Response(); }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = RunAsync(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start(); Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        void Reject(Action action, string name)
        { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
        var root = Path.GetFullPath(Path.Combine("Output", "KnowledgeAdviceChecks", Guid.NewGuid().ToString("N")));
        var store = new ClientStore(root); var fake = new FakeClient(); var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Response = () => pending.Task;
        var window = new MainWindow(store, fake);
        void Call(string method, params object[] args)
        {
            try { typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }
        var product = new CatalogProduct { SellerId = "测试店 A", ProductId = "123", Name = "演示收纳袋", Material = "棉帆布", ShippingTime = "48 小时内发出", Approved = true };
        try
        {
            Call("EditProduct", product); Call("SaveProduct");
            var saved = store.Products().Single(); var savedJson = JsonSerializer.Serialize(store.Products());
            Check(fake.Calls == 1 && saved.Name == product.Name && Field<StackPanel>(window, "_productEditor").Visibility == Visibility.Collapsed,
                "product save completes and clears editor before pending advice response");
            Check(Field<TextBlock>(window, "_adviceStatus").Text.Contains("正在检查"), "automatic advice has its own nonblocking progress state");
            pending.SetResult(Fixture); await Field<Task>(window, "_adviceTask");
            var service = new KnowledgeAdviceService(store, fake); var state = service.Load(saved);
            Check(state.Suggestions.Length == 2 && Field<StackPanel>(window, "_adviceItems").Children.Count == 2, "saved product automatically shows bounded actionable suggestions");
            Check(JsonSerializer.Serialize(store.Products()) == savedJson && store.Products().Single().Approved,
                "advice report never changes saved product facts or approval");
            fake.Response = () => Task.FromResult(Fixture);
            Call("EditProduct", saved); Call("SaveProduct"); await Field<Task>(window, "_adviceTask");
            Check(fake.Calls == 1, "saving identical content reuses advice cache despite timestamp changes");
            saved = store.Products().Single(); state = service.Load(saved);
            Call("ApplyAdvice", saved, state.ContentHash, state.Suggestions[1], true);
            var faqs = Field<KnowledgeRowsEditor>(window, "_faqs");
            Check(faqs.Values().Single()[0] == state.Suggestions[1].Question && faqs.Values().Single()[1] == "" && Field<CheckBox>(window, "_approved").IsChecked == false,
                "add-to-FAQ inserts only a question with a blank answer and revokes editor approval");
            Check(store.Products().Single().Faqs.Length == 0, "adding a question stays in editor until human completion and save");
            Call("ApplyAdvice", saved, state.ContentHash, state.Suggestions[1], true);
            Check(faqs.Values().Length == 1, "repeated add-to-FAQ does not duplicate the same question");
            Reject(() => Call("SaveProduct"), "unanswered FAQ cannot be silently saved as usable knowledge");
            Field<List<TextBox[]>>(faqs, "_inputs")[0][1].Text = "人工确认后填写的清洁说明。";
            Call("SaveProduct"); await Field<Task>(window, "_adviceTask");
            Check(fake.Calls == 2 && store.Products().Single().Faqs.Single().Answer == "人工确认后填写的清洁说明。", "human answer persists and changed facts trigger a new advice check");
            saved = store.Products().Single(); state = service.Load(saved); Call("EditProduct", saved);
            var fields = Field<Dictionary<string, TextBox>>(window, "_fields"); fields["Name"].Text = "正在编辑的名称";
            Call("ApplyAdvice", saved, state.ContentHash, state.Suggestions[0], false);
            Check(fields["Name"].Text == "正在编辑的名称" && fields["ReturnPolicy"].Text == "", "jumping to a field preserves same-product unsaved edits and does not fill a policy");
            var card = (Border)Field<StackPanel>(window, "_adviceItems").Children[0];
            ((StackPanel)card.Child).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b => Equals(b.Content, "忽略"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(new KnowledgeAdviceService(store).Load(saved).Ignored.Length == 1 && Field<StackPanel>(window, "_adviceItems").Children.Count == 1,
                "ignore button persists per-product choice and removes the visible card");
            await (Task)typeof(MainWindow).GetMethod("BeginAdviceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { saved, true })!;
            Check(fake.Contexts.Last().Contains("return_conditions") && service.Load(saved).Ignored.Length == 1 && Field<StackPanel>(window, "_adviceItems").Children.Count == 1,
                "rechecks include ignored topics and do not re-display them");
            Field<StackPanel>(window, "_adviceIgnored").Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(service.Load(saved).Ignored.Length == 0 && Field<StackPanel>(window, "_adviceItems").Children.Count == 2, "ignored suggestions can be restored without modifying product data");
            var other = saved with { SellerId = "测试店 B", Name = "另一店同编号商品" }; store.SaveProducts(new[] { saved, other });
            Check(service.Load(other).GeneratedAt == default && service.Load(other).Ignored.Length == 0, "same product ID in another shop has isolated advice and ignore state");
            var otherState = await service.CheckAsync(other, store.Settings(), false, CancellationToken.None);
            Call("SelectAdviceProduct", other);
            Reject(() => Call("ApplyAdvice", other, otherState.ContentHash, otherState.Suggestions[0], false), "advice action cannot discard another product's unsaved edit");
            Check(fields["Name"].Text == "正在编辑的名称", "cross-product navigation leaves manual draft intact");
            Call("SelectAdviceProduct", saved); Call("EditProduct", saved);
            var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); fake.Response = () => late.Task;
            var stale = service.CheckAsync(saved, store.Settings(), true, CancellationToken.None);
            var changed = saved with { Material = "新人工资料" }; store.SaveProducts(new[] { changed, other }); late.SetResult(Fixture);
            try { await stale; throw new Exception("Expected stale rejection"); } catch (InvalidOperationException) { Check(service.Load(saved).ContentHash == state.ContentHash, "late advice cannot overwrite a report after product facts changed"); }
            fake.Response = () => Task.FromException<string>(new IOException("测试服务故障"));
            Call("EditProduct", changed); Call("SaveProduct"); await Field<Task>(window, "_adviceTask");
            Check(store.Products().Single(p => p.SellerId == changed.SellerId).Material == changed.Material && Field<TextBlock>(window, "_adviceStatus").Text.Contains("商品已保存"),
                "AI failure does not roll back or misreport a successful product save");
            Reject(() => KnowledgeAdviceService.Parse(Fixture.Replace("\"reason\":", "\"answer\":\"编造答案\",\"reason\":")), "answer-bearing model output is rejected instead of imported");
            Reject(() => KnowledgeAdviceService.Parse(Fixture.Replace("ReturnPolicy", "UnknownField")), "unrecognized navigation targets are rejected");
            // Re-render the real page with the editor visible and the sidecar suggestions beside it.
            store.SaveProducts(new[] { saved, other }); fake.Response = () => Task.FromResult(Fixture);
            Call("EditProduct", saved); Call("SelectAdviceProduct", saved);
            await (Task)typeof(MainWindow).GetMethod("BeginAdviceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { saved, true })!;
            Field<ContentControl>(window, "_page").Content = Field<UIElement>(window, "_knowledgePage");
            var page = (FrameworkElement)Field<UIElement>(window, "_knowledgePage");
            page.Measure(new Size(1030, 820)); page.Arrange(new Rect(0, 0, 1030, 820)); page.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1030, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, "knowledge-advice.png"))) encoder.Save(file);
            Check(Field<ScrollViewer>(window, "_knowledgeScroll").ExtentWidth <= Field<ScrollViewer>(window, "_knowledgeScroll").ViewportWidth + 1,
                "product form and advice sidebar fit without horizontal page scrolling");
            Field<KnowledgeRowsEditor>(window, "_variants").Load(new[] { new[] { "较长的测试规格名称", "128.00", "测试规格说明" } });
            page.Measure(new Size(740, 750)); page.Arrange(new Rect(0, 0, 740, 750)); page.UpdateLayout();
            Check(Field<ScrollViewer>(window, "_knowledgeScroll").ExtentWidth <= Field<ScrollViewer>(window, "_knowledgeScroll").ViewportWidth + 1,
                "minimum-width layout still fits specification rows beside advice");
            var cachedAt = service.Load(saved).GeneratedAt;
            var lateUi = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); fake.Response = () => lateUi.Task;
            var pendingUi = (Task)typeof(MainWindow).GetMethod("BeginAdviceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { saved, true })!;
            Call("SelectAdviceProduct", other); lateUi.SetResult(Fixture); await pendingUi;
            Check(Field<TextBlock>(window, "_adviceProductLabel").Text.Contains("测试店 B") && service.Load(saved).GeneratedAt == cachedAt,
                "switching products cancels old advice without changing the new sidebar or cached report");
            Console.WriteLine($"Passed {passed} knowledge advice checks. Preview: {root}"); return 0;
        }
        finally { window.Close(); }
    }
    internal static async Task<int> HttpAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var task = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync(); using var reader = new StreamReader(request.Request.InputStream);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var system = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            if (!system.Contains("商品资料查缺助手") || !system.Contains("不要生成商品问题的答案")) throw new Exception("Dedicated analysis instructions missing");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Fixture } } } }));
            await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
        });
        var raw = await new KnowledgeAdviceClient().GenerateAsync(new ClientSettings { AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "fixture" }, "{\"Facts\":{\"Product\":{\"Name\":\"测试商品\"}}}", CancellationToken.None);
        await task; if (KnowledgeAdviceService.Parse(raw).Length != 2) throw new Exception("Advice response did not parse");
        Console.WriteLine("PASS: configured chat transport uses dedicated advice instructions and strict question-only schema"); return 0;
    }
    internal static async Task<int> LiveAsync(string root)
    {
        var settings = new ClientStore(Path.Combine(root, "Output", "DesktopClient")).Settings();
        var test = new ClientStore(Path.Combine(root, "Output", "KnowledgeAdviceLive", Guid.NewGuid().ToString("N")));
        var product = new CatalogProduct { SellerId = "虚构测试店", ProductId = "123", Name = "演示帆布收纳袋", Material = "棉帆布", Capacity = "30×20cm", ShippingTime = "48小时内发出", Source = "虚构测试资料" };
        test.SaveProducts(new[] { product });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var report = await new KnowledgeAdviceService(test).CheckAsync(product, settings, false, deadline.Token);
        if (report.Suggestions.Length == 0 || report.Suggestions.Length > 8 || test.Products().Single().Faqs.Length != 0) throw new Exception("Live advice bounds or no-write policy failed");
        foreach (var item in report.Suggestions) Console.WriteLine($"{item.Status}/{item.Target}: {item.Question} | {item.SuggestedAction}");
        Console.WriteLine("Live synthetic advice check passed; no real product or buyer data used. Evidence: " + test.Root); return 0;
    }
}
