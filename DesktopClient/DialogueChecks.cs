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
using System.Windows.Threading;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class DialogueChecks
{
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = RunAsync(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start(); Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static async Task<int> RunAsync()
    {
        int passed = 0; void Check(bool pass, string name) { if (!pass) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        var root = Path.GetFullPath(Path.Combine("Output", "DialogueChecks", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        var lines = new List<OcrLine>();
        using var bitmap = new System.Drawing.Bitmap(800, 600);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(240, 242, 245));
            void Mark(string text, int x, int y, int width, int height, bool header)
            {
                graphics.FillRectangle(header ? System.Drawing.Brushes.Gray : System.Drawing.Brushes.Black, x, y, width, height);
                lines.Add(new OcrLine(lines.Count + 1, text, .99f, new OcrBounds(x, y, width, height)));
            }
            void Bubble(bool seller, int y, string text, int second, bool split = false)
            {
                var x = seller ? 420 : 100;
                using var brush = new System.Drawing.SolidBrush(seller ? System.Drawing.Color.FromArgb(230, 240, 255) : System.Drawing.Color.White);
                graphics.FillRectangle(brush, x, y, 300, 50);
                if (seller && split)
                { Mark($"2026-10-24 12:00:0{second}", 300, y - 27, 220, 17, true); Mark("shop-agent", 535, y - 27, 185, 17, true); }
                else Mark(seller ? $"2026-10-24 12:00:0{second} shop-agent" : $"buyer-one 2026-10-24 12:00:0{second}", seller ? 300 : 100, y - 27, seller ? 420 : 360, 17, true);
                Mark(text, x + 12, y + 12, 250, 20, false);
            }
            Bubble(false, 55, "我想了解规格", 0); Bubble(true, 185, "您要大款还是小款？", 1);
            Bubble(false, 315, "小款", 2); Bubble(true, 465, "好的，我确认一下", 3, true);
        }
        using var imageStream = new MemoryStream(); bitmap.Save(imageStream, System.Drawing.Imaging.ImageFormat.Png); var png = imageStream.ToArray();
        File.WriteAllBytes(Path.Combine(root, "dialogue-fixture.png"), png);
        var ocr = new OcrResult("memory:dialogue", "fixture", lines, 0, null, 0, 0, 0, 0, 0, 0);
        var batch = new BuyerMessageStructurer(true, true).Structure(ocr, png);
        Check(batch.Messages.Select(m => m.Role).SequenceEqual(new[] { "buyer", "seller", "buyer", "seller" }), "white incoming and blue outgoing bubbles retain their roles and visual order");
        Check(batch.Messages[1].Text.Contains("大款") && batch.Messages[3].Text.Contains("确认"), "combined and split outgoing sender/time headers are accepted");
        Check(SessionRegistry.ReadBuyer(batch, ocr) == "buyer-one", "seller nickname is never used as buyer identity");
        var lowConfidence = ocr with { Lines = lines.Select(l => l.Text == "shop-agent" ? l with { Confidence = .2f } : l).ToArray() };
        Check(new BuyerMessageStructurer(true, true).Structure(lowConfidence, png).Messages.Count == 3, "unreliable seller header stays review-only");
        Check(new BuyerMessageStructurer().Structure(ocr, png).Messages.All(m => m.Role == "buyer"), "legacy buyer-only parsing remains opt-in compatible");
        using (var mat = OpenCvSharp.Cv2.ImDecode(png, OpenCvSharp.ImreadModes.Color))
            Check(MessageVisualRegions.OcrRegion(mat).Bottom >= 515, "focused OCR includes the newest seller bubble at the bottom");
        var shared = new ClientStore(root); shared.SaveProducts(new[] { new CatalogProduct { SellerId = "shop", ProductId = "111", Name = "测试收纳盒", Approved = true } });
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        shared.SaveSettings(new ClientSettings { SellerId = "shop", AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "fixture" });
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        int requests = 0; var payloads = new List<string>(); var workers = new List<Task>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await listener.GetContextAsync(); workers.Add(Task.Run(async () =>
                    {
                        using var reader = new StreamReader(request.Request.InputStream);
                        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                        var payload = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                        lock (payloads) payloads.Add(payload); Interlocked.Increment(ref requests);
                        using var parsed = JsonDocument.Parse(payload);
                        if (parsed.RootElement.GetProperty("BuyerQuestion").GetString() == "等待中的问题") { held.SetResult(); await release.Task; }
                        var bytes = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"已经参考双方对话的建议\"}}]}");
                        try { await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close(); } catch (HttpListenerException) { }
                    }));
                }
            }
            catch (Exception e) when (!listener.IsListening && e is HttpListenerException or ObjectDisposedException) { }
        });
        var target = new QianniuWindow("fixture", Environment.ProcessId, 101, 101, "fixture", new System.Drawing.Rectangle(0, 0, 800, 600), true, false, false);
        var registry = new WindowBindingRegistry(shared); var binding = registry.GetOrCreate(target, "shop", "fixture"); var store = registry.OpenStore(binding);
        var session = new ReceptionSession(shared, store, binding, target, new ResourceDictionary(), _ => { }, _ => { }, new FixtureJevGateway());
        var connection = Field<QianniuConnection>(session, "_connection"); var context = Field<AgentContextPipeline>(connection, "_context");
        var sellerOnly = batch with { Messages = batch.Messages.Where(m => m.Role == "seller").ToArray(),
            Evidence = batch.Evidence.Where(e => batch.Messages[e.MessageIndex].Role == "seller").Select((e, i) => e with { MessageIndex = i }).ToArray() };
        typeof(QianniuConnection).GetField("_confirmedBuyer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, "buyer-one");
        typeof(QianniuConnection).GetField("_confirmedIdentityFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, "header-one");
        typeof(QianniuConnection).GetField("_confirmedInteraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, 7L);
        Check(connection.ResolveObservedBuyer(sellerOnly, ocr, "header-one", 7) == "buyer-one", "seller-only viewport can retain a previously proven unchanged buyer header");
        Check(connection.ResolveObservedBuyer(sellerOnly, ocr, "header-two", 7) is null && connection.ResolveObservedBuyer(sellerOnly, ocr, "header-one", 8) is null,
            "changed header or switch epoch cannot reuse seller-only buyer identity");
        int seq = 0;
        InboxMessage Message(string role, string text, bool baseline = false, string buyer = "buyer-one")
        {
            var time = new DateTime(2026, 10, 24, 12, 0, 0).AddSeconds(++seq);
            var state = context.ProcessMessage(new MessageEnvelope("Qianniu.OCR", "shop", buyer, "111", new[] { new EnvelopeMessage(role, text, time) }));
            var last = state.Messages.Last();
            return new InboxMessage(state.SessionId, last.MessageId, "shop", buyer, text, time, "111", "fixture", baseline)
                { Role = role, Sequence = seq, WindowScopeId = binding.ScopeId };
        }
        void Publish(InboxMessage message) { session.AcceptMessage(message); session.ObserveConversation(new("shop", message.Buyer, "当前会话")); }
        async Task Until(Func<bool> condition) { using var deadline = new CancellationTokenSource(7000); while (!condition()) await Task.Delay(20, deadline.Token); }
        var reply = Field<TextBox>(session, "_reply"); var question = Field<TextBlock>(session, "_question"); var service = Field<ReplyService>(session, "_replies");
        try
        {
            var initial = Message("buyer", "首屏历史问题", true); Publish(initial); await Task.Delay(650);
            Check(requests == 0, "loading baseline history never automatically generates old answers");
            reply.Text = "请保留这份手工草稿";
            var productBefore = Field<ComboBox>(session, "_replyProduct").SelectedItem;
            session.ObserveConversation(new("shop", null, "画面变化", PreserveDisplay: true));
            Check(reply.Text == "请保留这份手工草稿" && question.Text == initial.Text && ReferenceEquals(productBefore, Field<ComboBox>(session, "_replyProduct").SelectedItem),
                "body-only pending refresh preserves buyer question, product and draft");
            Publish(Message("seller", "您要大款还是小款？")); await Task.Delay(650);
            Check(requests == 0 && question.Text == initial.Text && reply.Text == "请保留这份手工草稿", "seller reply updates history without clearing draft or generating a new answer");
            Check(Field<ListBox>(session, "_inbox").Items.Cast<InboxMessage>().Any(m => m.Role == "seller" && m.DisplayText.StartsWith("卖家")), "recent-history UI explicitly labels seller messages");
            var incoming = Message("buyer", "小款"); Publish(incoming); await Until(() => service.Latest(incoming) is not null);
            using (var input = JsonDocument.Parse(payloads.Last()))
            {
                var dialogue = input.RootElement.GetProperty("Conversation").EnumerateArray().ToArray();
                Check(dialogue.Length == 3 && dialogue[1].GetProperty("Role").GetString() == "seller" && dialogue[1].GetProperty("Text").GetString()!.Contains("大款"),
                    "new buyer reply invokes the model with preceding seller question and both roles");
            }
            reply.Text = "继续保留的草稿"; var priorRequests = requests;
            session.ObserveConversation(new("shop", null, "正在识别卖家发送", PreserveDisplay: true));
            Publish(Message("seller", "小款可以，稍等我确认")); await Task.Delay(650);
            Check(requests == priorRequests && reply.Text == "继续保留的草稿" && question.Text == incoming.Text, "seller update leaves generated view and manual edits intact");
            var pending = Message("buyer", "等待中的问题"); Publish(pending); await held.Task.WaitAsync(TimeSpan.FromSeconds(7));
            reply.Text = "正在手工处理这条问题";
            session.ObserveConversation(new("shop", null, "内容更新", PreserveDisplay: true));
            Publish(Message("seller", "我已经答复您了")); release.SetResult(); await Task.Delay(750);
            Check(reply.Text == "正在手工处理这条问题" && service.Latest(pending) is null, "seller reply cancels an in-flight old answer without overwriting the draft");
            var afterSeller = requests; session.ObserveConversation(new("shop", "buyer-one", "重复画面")); await Task.Delay(650);
            Check(requests == afterSeller, "seller-only refresh never retries the cancelled buyer answer automatically");
            var automatic = Field<CheckBox>(session, "_automatic"); automatic.IsChecked = false;
            for (var i = 0; i < 14; i++) Publish(Message(i % 2 == 0 ? "seller" : "buyer", "顺序消息" + i, true));
            var selected = Field<InboxMessage>(session, "_selected"); var bounded = connection.Context(selected);
            Check(bounded.Messages.Count == 10 && bounded.Messages[0].Text == "顺序消息4" && bounded.Messages.Last().Text == "顺序消息13" &&
                bounded.Messages.Count(m => m.Role == "seller") == 5, "model context contains exactly the latest ten ordered buyer/seller messages");
            Check(AgentContextIntentAdapter.Build(context.GetSession(selected.SessionId)!, selected.MessageId, DateTimeOffset.Now).Messages.Count == 4,
                "legacy non-desktop intent callers retain their original bounded context");
            Check(Field<ListBox>(session, "_inbox").Items.Count == 10, "visible recent history is limited to ten dialogue messages");
            Publish(Message("buyer", "另一个买家的问题", true, "buyer-two"));
            var other = connection.Context(Field<InboxMessage>(session, "_selected"));
            Check(other.Messages.Count == 1 && !other.Messages.Any(m => m.Text.Contains("顺序消息")), "buyer switch cannot borrow either party's history from the previous buyer");
            Check(reply.Text == "" && question.Text == "另一个买家的问题", "real buyer switching still clears the old buyer draft display");
            Console.WriteLine($"Passed {passed} bilateral dialogue checks. Evidence: {root}"); return 0;
        }
        finally { release.TrySetResult(); await session.StopAsync(); listener.Stop(); await server; await Task.WhenAll(workers); }
    }
    internal static async Task<int> CaptureLiveAsync()
    {
        var target = QianniuConnection.Windows().FirstOrDefault(w => w.IsVisible && !w.IsMinimized) ?? throw new InvalidOperationException("没有可见千牛接待窗口。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using var worker = await MemoryNativeWorker.StartAsync(timeout.Token);
        var capture = await worker.CaptureAsync(target, timeout.Token);
        if (capture.Png is null) throw new InvalidOperationException("未取得有效画面。");
        var frame = QnocrPoc.Capture.MemoryChatFrame.FromPng(capture.Png, includeProduct: false);
        var ocr = await worker.RecognizeChatAsync(frame.Chat, "memory:dialogue-verification", timeout.Token);
        var batch = new BuyerMessageStructurer(true, true).Structure(ocr, frame.Chat);
        Console.WriteLine($"Live in-memory recognition: buyerMessages={batch.Messages.Count(m => m.Role == "buyer")}, sellerMessages={batch.Messages.Count(m => m.Role == "seller")}, buyerBound={SessionRegistry.ReadBuyer(batch, ocr) is not null}, reviewItems={batch.ReviewItems.Count}");
        if (SessionRegistry.ReadBuyer(batch, ocr) is null)
        {
            Console.WriteLine("Layout geometry: " + JsonSerializer.Serialize(new { frame.Layout.ScreenshotSize, frame.Layout.ChatRegion }));
            Console.WriteLine("Review reasons: " + JsonSerializer.Serialize(batch.ReviewItems.GroupBy(r => r.Reason).Select(g => new { Reason = g.Key, Count = g.Count() })));
        }
        Console.WriteLine("No screenshots or chat text saved; no external model called and no messages sent."); return 0;
    }
    internal static async Task<int> NativeOcrAsync()
    {
        using var image = new System.Drawing.Bitmap(900, 650);
        using (var graphics = System.Drawing.Graphics.FromImage(image))
        using (var headerFont = new System.Drawing.Font("Microsoft YaHei", 20, System.Drawing.GraphicsUnit.Pixel))
        using (var bodyFont = new System.Drawing.Font("Microsoft YaHei", 23, System.Drawing.GraphicsUnit.Pixel))
        using (var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(230, 240, 255)))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(240, 242, 245));
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("buyer-one 2026-10-24 12:00:00", headerFont, System.Drawing.Brushes.Gray, 100, 60);
            graphics.FillRectangle(System.Drawing.Brushes.White, 100, 95, 400, 65);
            graphics.DrawString("请问大款和小款有什么区别", bodyFont, System.Drawing.Brushes.Black, 112, 110);
            graphics.DrawString("2026-10-24 12:00:01 shop-agent", headerFont, System.Drawing.Brushes.Gray, 440, 230);
            graphics.FillRectangle(blue, 470, 265, 340, 65);
            graphics.DrawString("您更需要哪一种尺寸？", bodyFont, System.Drawing.Brushes.Black, 482, 280);
        }
        using var png = new MemoryStream(); image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var worker = await MemoryNativeWorker.StartAsync(timeout.Token);
        var result = await worker.RecognizeChatAsync(png.ToArray(), "memory:synthetic-dialogue", timeout.Token);
        var batch = new BuyerMessageStructurer(true, true).Structure(result, png.ToArray());
        Console.WriteLine($"Synthetic native OCR: buyer={batch.Messages.Count(m => m.Role == "buyer")}, seller={batch.Messages.Count(m => m.Role == "seller")}, buyerBound={SessionRegistry.ReadBuyer(batch, result) is not null}");
        if (batch.Messages.Count(m => m.Role == "buyer") != 1 || batch.Messages.Count(m => m.Role == "seller") != 1 || SessionRegistry.ReadBuyer(batch, result) is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(batch.ReviewItems));
            throw new Exception("Native OCR did not preserve both synthetic dialogue roles.");
        }
        Console.WriteLine("PASS: native focused OCR and role structuring recognize both parties without external model calls."); return 0;
    }
}
