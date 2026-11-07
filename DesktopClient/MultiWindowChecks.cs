using System;
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
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class MultiWindowChecks
{
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = RunAsync(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start();
        Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Task Generate(ReceptionSession session) => (Task)typeof(ReceptionSession).GetMethod("GenerateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
    private static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
        var root = Path.GetFullPath(Path.Combine("Output", "MultiWindowChecks", Guid.NewGuid().ToString("N")));
        var shared = new ClientStore(root); var registry = new WindowBindingRegistry(shared);
        var targetA = new QianniuWindow("controlled", Environment.ProcessId, 101, 101, "相同标题", new System.Drawing.Rectangle(0, 0, 800, 640), true, false, false);
        var targetB = targetA with { Hwnd = 102 };
        var bindA = registry.GetOrCreate(targetA, "shop", "process-start:101");
        var bindB = registry.GetOrCreate(targetB, "shop", "process-start:102");
        Check(bindA.ScopeId != bindB.ScopeId, "same seller/title in two windows gets different storage scopes");
        Check(registry.GetOrCreate(targetA, "shop", "process-start:101").ScopeId == bindA.ScopeId, "reconnecting the same live window restores only its scope");
        Check(registry.GetOrCreate(targetA, "shop", "new-process-start:101").ScopeId != bindA.ScopeId, "reused HWND after process restart cannot inherit another window scope");
        Check(registry.GetOrCreate(targetA, "other-shop", "process-start:101").ScopeId != bindA.ScopeId, "rebinding to another seller uses a separate scope");
        Reject(() => registry.OpenStore(bindA with { ScopeId = "../outside" }), "invalid scope cannot escape the data directory");
        var stateA = registry.OpenStore(bindA); var stateB = registry.OpenStore(bindB);
        Reject(() => new ReceptionSession(shared, stateA, bindB, targetB, new ResourceDictionary(), _ => { }, _ => { }),
            "controller refuses another window's physical state directory");
        var productA = new CatalogProduct { SellerId = "shop", ProductId = "11111111111", Name = "商品 A", Material = "材质 A", Source = "受控核对", Approved = true };
        var productB = productA with { ProductId = "22222222222", Name = "商品 B", Material = "材质 B" };
        shared.SaveProducts(new[] { productA, productB, productA with { SellerId = "other-shop", Name = "其他店商品" } });
        var shopOffer = new Promotion { Title = "共用店铺券", AppliesTo = "全店", Rule = "满 100 减 5", Validity = "待核实", Stacking = "不叠加" };
        shared.SaveShop(new ShopKnowledge { SellerId = "shop", Approved = true, Promotions = new[] { shopOffer } });
        shared.SaveShop(new ShopKnowledge { SellerId = "settings-last-shop", Approved = true, Promotions = new[] { shopOffer with { Title = "其他店优惠" } } });
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        shared.SaveSettings(new ClientSettings { SellerId = "settings-last-shop", AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "controlled-model" });
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var aArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var requests = new System.Collections.Generic.List<Task>();
            for (var i = 0; i < 2; i++)
            {
                var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(15));
                requests.Add(Task.Run(async () =>
                {
                    using var reader = new StreamReader(request.Request.InputStream);
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                    using var payload = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                    var product = payload.RootElement.GetProperty("Product");
                    var id = product.GetProperty("ProductId").GetString();
                    if (payload.RootElement.GetProperty("Seller").GetString() != "shop") throw new Exception("Shared selected seller leaked into window request");
                    if (payload.RootElement.GetProperty("ShopPromotions").GetProperty("Promotions")[0].GetProperty("Title").GetString() != "共用店铺券")
                        throw new Exception("Window did not load shared promotions for its own seller");
                    var isA = id == productA.ProductId;
                    if (product.GetProperty("Fields").GetProperty("Material").GetString() != (isA ? "材质 A" : "材质 B")) throw new Exception("Product facts crossed windows");
                    if (isA) { aArrived.SetResult(); await releaseA.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
                    var text = isA ? "窗口 A 的建议" : "窗口 B 的建议";
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }));
                    request.Response.ContentType = "application/json";
                    try { await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close(); }
                    catch (HttpListenerException) when (isA) { } // Buyer A was deliberately cancelled while its response was held.
                }));
            }
            await Task.WhenAll(requests);
        });
        var a = new ReceptionSession(shared, stateA, bindA, targetA, new ResourceDictionary(), _ => { }, _ => { }, new FixtureJevGateway(), autoGenerate: false);
        var b = new ReceptionSession(shared, stateB, bindB, targetB, new ResourceDictionary(), _ => { }, _ => { }, new FixtureJevGateway(), autoGenerate: false);
        try
        {
            InboxMessage Seed(ReceptionSession session, string buyer, string text)
            {
                var context = Field<AgentContextPipeline>(Field<QianniuConnection>(session, "_connection"), "_context");
                var snapshot = context.ProcessMessage(new MessageEnvelope("Qianniu.OCR", session.SellerId, buyer, "",
                    new[] { new EnvelopeMessage("buyer", text, new DateTime(2026, 10, 23, 12, 0, 0)) }));
                var last = snapshot.Messages.Last();
                return new(snapshot.SessionId, last.MessageId, session.SellerId, buyer, text, last.Timestamp, "", "memory:controlled", false) { WindowScopeId = session.ScopeId };
            }
            var messageA = Seed(a, "同名买家", "这款是什么材质？"); var messageB = Seed(b, "同名买家", "这款是什么材质？");
            Check(a.AcceptMessage(messageA) && b.AcceptMessage(messageB), "each window accepts its scoped message");
            Check(!a.AcceptMessage(messageB) && !b.AcceptMessage(messageA), "identical buyer/message IDs cannot cross window boundaries");
            a.ObserveConversation(new("shop", "同名买家", "窗口 A")); b.ObserveConversation(new("shop", "同名买家", "窗口 B"));
            var productsA = Field<ComboBox>(a, "_replyProduct"); var productsB = Field<ComboBox>(b, "_replyProduct");
            Check(((CatalogProduct[])productsA.ItemsSource).All(p => p.SellerId == "shop"), "catalog remains shared but selections only contain the bound seller");
            productsA.SelectedItem = ((CatalogProduct[])productsA.ItemsSource).Single(p => p.ProductId == productA.ProductId);
            productsB.SelectedItem = ((CatalogProduct[])productsB.ItemsSource).Single(p => p.ProductId == productB.ProductId);
            Check(((CatalogProduct)productsA.SelectedItem).ProductId != ((CatalogProduct)productsB.SelectedItem).ProductId, "two windows retain independent product selection");
            var pendingA = Generate(a); await aArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Generate(b).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Field<TextBox>(b, "_reply").Text == "窗口 B 的建议" && Field<TextBox>(a, "_reply").Text == "", "B completes while A waits, without publishing into A");
            var newBuyerA = Seed(a, "窗口 A 的另一买家", "新的问题"); a.AcceptMessage(newBuyerA);
            a.ObserveConversation(new("shop", "窗口 A 的另一买家", "已经切换"));
            releaseA.SetResult();
            try { await pendingA.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Field<TextBox>(a, "_reply").Text != "窗口 A 的建议" && Field<TextBox>(b, "_reply").Text == "窗口 B 的建议", "late A response cannot overwrite new buyer or window B");
            var repliesA = Field<ReplyService>(a, "_replies"); var repliesB = Field<ReplyService>(b, "_replies");
            Check(repliesA.Latest(messageA) is null && repliesB.Latest(messageB)?.AiReply == "窗口 B 的建议", "cancelled A does not persist stale replies and B keeps its own reply index");
            Reject(() => repliesB.Latest(messageA), "reply service rejects a foreign window scope");
            Check(stateA.Messages().All(m => m.WindowScopeId == bindA.ScopeId) && stateB.Messages().All(m => m.WindowScopeId == bindB.ScopeId), "inbox persistence never overwrites the other window");
            Check(shared.Settings().SellerId == "settings-last-shop" && shared.Products().Count == 3, "window processing preserves shared settings and catalog");
            Field<TextBox>(b, "_reply").Text = "窗口 B 的人工草稿";
            SavePreview(a, b, Path.Combine(root, "multi-window-preview.png"));
            await a.StopAsync();
            Check(a.IsStopped && !b.IsStopped && Field<TextBox>(b, "_reply").Text == "窗口 B 的人工草稿" &&
                ((CatalogProduct)productsB.SelectedItem).ProductId == productB.ProductId, "disconnecting A leaves B's draft, product and controller untouched");
            Check(!a.AcceptMessage(messageA), "disconnected window ignores late messages");
        }
        finally { releaseA.TrySetResult(); await a.StopAsync(); await b.StopAsync(); }
        byte[] Image(string text)
        {
            using var bitmap = new System.Drawing.Bitmap(620, 180);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Arial", 30))
            { graphics.Clear(System.Drawing.Color.White); graphics.DrawString(text, font, System.Drawing.Brushes.Black, 30, 60); }
            using var png = new MemoryStream(); bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); return png.ToArray();
        }
        await using var workerA = await MemoryNativeWorker.StartAsync(CancellationToken.None);
        await using var workerB = await MemoryNativeWorker.StartAsync(CancellationToken.None);
        var images = new[] { Image("WINDOW A 12345"), Image("WINDOW B 98765") };
        var results = await Task.WhenAll(workerA.RecognizeAsync(images[0], "memory:window-A", CancellationToken.None),
            workerB.RecognizeAsync(images[1], "memory:window-B", CancellationToken.None));
        Check(results[0].ImagePath == "memory:window-A" && results[0].Text.Contains("12345") && !results[0].Text.Contains("98765") &&
            results[1].ImagePath == "memory:window-B" && results[1].Text.Contains("98765") && !results[1].Text.Contains("12345"),
            "simultaneous native OCR processes keep pixels and responses isolated");
        await workerA.DisposeAsync();
        Check((await workerB.RecognizeAsync(images[1], "memory:B-after-A-stopped", CancellationToken.None)).Text.Contains("98765"),
            "stopping native worker A does not stop worker B");
        Console.WriteLine($"Passed {count} multi-window isolation checks. Evidence: {root}"); return 0;
    }
    private static void SavePreview(ReceptionSession a, ReceptionSession b, string path)
    {
        BitmapSource Render(ReceptionSession session)
        {
            var visual = (FrameworkElement)session.Panel.Content;
            visual.Measure(new Size(390, 720)); visual.Arrange(new Rect(0, 0, 390, 720)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(390, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
        }
        var combined = new DrawingVisual(); using (var drawing = combined.RenderOpen())
        { drawing.DrawImage(Render(a), new Rect(0, 0, 390, 720)); drawing.DrawImage(Render(b), new Rect(400, 0, 390, 720)); }
        var image = new RenderTargetBitmap(790, 720, 96, 96, PixelFormats.Pbgra32); image.Render(combined);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(path); png.Save(file);
    }
}
