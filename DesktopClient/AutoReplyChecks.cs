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
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class AutoReplyChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard { public string Text = ""; public void SetText(string text) => Text = text; }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = RunAsync(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start();
        Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static async Task<int> RunAsync()
    {
        int passed = 0; void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        async Task Until(Func<bool> condition)
        { using var timeout = new CancellationTokenSource(8000); while (!condition()) await Task.Delay(20, timeout.Token); }
        var root = Path.GetFullPath(Path.Combine("Output", "AutoReplyChecks", Guid.NewGuid().ToString("N")));
        var shared = new ClientStore(root);
        var product = new CatalogProduct { SellerId = "shop", ProductId = "111", Name = "测试烟盒", SalesScope = "只销售收纳盒，不含内装物品", Material = "测试材质", Approved = true };
        shared.SaveProducts(new[] { product, product with { ProductId = "222", Name = "商品 B" }, product with { ProductId = "333", Approved = false } });
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        shared.SaveSettings(new ClientSettings { SellerId = "shop", AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "fixture" });
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        int requests = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEdit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivedEdit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new List<Task>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await listener.GetContextAsync();
                    workers.Add(Task.Run(async () =>
                    {
                        using var reader = new StreamReader(request.Request.InputStream);
                        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                        using var prompt = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                        var question = prompt.RootElement.GetProperty("BuyerQuestion").GetString();
                        var n = Interlocked.Increment(ref requests);
                        if (question == "延迟问题") { arrived.SetResult(); await release.Task; }
                        if (question == "编辑中问题") { arrivedEdit.SetResult(); await releaseEdit.Task; }
                        if (question == "失败问题") { request.Response.StatusCode = 500; request.Response.Close(); return; }
                        var replies = new[] { $"建议{n}：这是收纳用的烟盒。", $"建议{n}：您好，这款是收纳盒。", $"建议{n}：您想了解这款烟盒的哪方面呢？" };
                        var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = replies[0] } } } }));
                        try { await request.Response.OutputStream.WriteAsync(data); request.Response.Close(); }
                        catch (HttpListenerException) { } // A switched-away buyer cancels the HTTP client.
                    }));
                }
            }
            catch (Exception e) when (!listener.IsListening && e is HttpListenerException or ObjectDisposedException) { }
        });
        var target = new QianniuWindow("fixture", Environment.ProcessId, 100, 100, "测试窗口", new System.Drawing.Rectangle(0, 0, 800, 640), true, false, false);
        var registry = new WindowBindingRegistry(shared); var binding = registry.GetOrCreate(target, "shop", "fixture");
        var session = new ReceptionSession(shared, registry.OpenStore(binding), binding, target, new ResourceDictionary(), _ => { }, _ => { }, new FixtureJevGateway());
        var context = Field<AgentContextPipeline>(Field<QianniuConnection>(session, "_connection"), "_context");
        int sequence = 0;
        InboxMessage Seed(string buyer, string text, string id = "")
        {
            var now = new DateTime(2026, 10, 23, 12, 0, 0).AddSeconds(++sequence);
            var state = context.ProcessMessage(new MessageEnvelope("Qianniu.OCR", "shop", buyer, id, new[] { new EnvelopeMessage("buyer", text, now) }));
            var last = state.Messages.Last();
            var message = new InboxMessage(state.SessionId, last.MessageId, "shop", buyer, text, now, id, "fixture", false) { WindowScopeId = binding.ScopeId };
            session.AcceptMessage(message); session.ObserveConversation(new("shop", buyer, "当前买家")); return message;
        }
        var products = Field<ComboBox>(session, "_replyProduct");
        var editor = Field<TextBox>(session, "_reply"); var automatic = Field<CheckBox>(session, "_automatic");
        var service = Field<ReplyService>(session, "_replies");
        try
        {
            var first = Seed("买家 A", "这是一条还是一盒？"); await Task.Delay(650);
            Check(requests == 0, "auto generation waits for a confirmed product");
            var stateStore = registry.OpenStore(binding); var legacyId = Guid.NewGuid().ToString("N");
            var legacyContext = Field<QianniuConnection>(session, "_connection").Context(first) with { ProductId = "111" };
            var legacy = new ReplyPreview(first.SessionId, "买家 A", legacyContext, "旧流程的固定核实回复", null,
                DateTimeOffset.Now.AddHours(-1), "Shadow", "fixture", "old-request", "old-response", legacyId);
            service.Assistant.InitializePreview(Path.Combine(stateStore.ReviewRoot, "outbox", legacyId), legacy);
            stateStore.Write(Path.Combine("reviews", "outbox", legacyId, "reply-preview.json"), legacy);
            stateStore.Write(Path.Combine("reply-index", first.MessageId.Replace(':', '_') + ".json"), new { PreviewId = legacyId });
            var old = service.Assistant.Show(legacyId)!;
            service.Assistant.Approve(legacyId, "test", old.ReplySha256);
            service.Assistant.Copy(legacyId, "test", old.ReplySha256, new ClipboardProbe());
            products.SelectedItem = ((CatalogProduct[])products.ItemsSource).Single(p => p.ProductId == "111");
            await Until(() => editor.Text.Contains("建议1"));
            Check(requests == 1 && editor.Text.Contains("建议1"), "selecting an approved product automatically generates one reply");
            Check(service.Latest(first)?.ReplyPreviewId != legacyId && service.Assistant.Show(legacyId)?.Status == "Copied", "old copied replies are refreshed by new pipeline while preserving history");
            Check(Field<TextBlock>(session, "_callStatus").Text.Contains("回复模型已调用"), "UI reports completed JEV and reply-model calls");
            for (var i = 0; i < 4; i++) session.ObserveConversation(new("shop", "买家 A", "刷新"));
            await Task.Delay(650); Check(requests == 1, "repeated OCR observations do not regenerate the same message");
            var selectedText = editor.Text; var selected = service.Latest(first)!;
            var clipboard = new ClipboardProbe(); service.ConfirmAndCopy(first, selected.ReplyPreviewId, selectedText, clipboard);
            Check(clipboard.Text == selectedText && service.Options(first).Length == 1, "single displayed reply is confirmed and copied");
            editor.Text = "手工草稿"; session.ObserveConversation(new("shop", "买家 A", "刷新"));
            Check(editor.Text == "手工草稿", "routine observations preserve a manual draft");
            var second = Seed("买家 A", "新问题", "111"); await Until(() => editor.Text.Contains("建议2"));
            Check(requests == 2, "a new buyer message generates new candidates without a click");
            products.SelectedItem = ((CatalogProduct[])products.ItemsSource).Single(p => p.ProductId == "222");
            await Task.Delay(650); Check(requests == 2, "product reselection alone does not trigger another automatic answer");
            await (Task)typeof(ReceptionSession).GetMethod("GenerateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
            await Until(() => editor.Text.Contains("建议3"));
            Check(service.Latest(second)?.CurrentProductId == "222", "product changes generate replies bound to the new product");
            var stale = Seed("买家 A", "延迟问题", "111"); await arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var current = Seed("买家 B", "当前问题", "111"); release.SetResult();
            await Until(() => editor.Text.Contains("建议5"));
            Check(service.Latest(stale) is null && service.Latest(current) is not null, "buyer switching cancels old generation without persisting stale alternatives");
            Seed("买家 B", "编辑中问题", "111"); await arrivedEdit.Task.WaitAsync(TimeSpan.FromSeconds(8));
            editor.Text = "请保留我正在写的内容"; releaseEdit.SetResult();
            await Until(() => service.Latest(Field<InboxMessage>(session, "_selected")) is not null);
            Check(editor.Text == "请保留我正在写的内容", "automatic completion does not overwrite text typed while waiting");
            var draftMessage = Field<InboxMessage>(session, "_selected"); service.ConfirmAndCopy(draftMessage, service.Latest(draftMessage)!.ReplyPreviewId, editor.Text, clipboard);
            Check(clipboard.Text == "请保留我正在写的内容", "draft typed during generation can be confirmed as the current single reply");
            automatic.IsChecked = false; Seed("买家 B", "已暂停", "111"); await Task.Delay(650);
            Check(requests == 6, "per-window automatic generation can be paused");
            await (Task)typeof(ReceptionSession).GetMethod("GenerateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
            Check(requests == 7 && service.Options(Field<InboxMessage>(session, "_selected")).Length == 1, "manual regeneration still works while automatic mode is paused");
            shared.SaveProducts(shared.Products().Select(p => p.ProductId == "111" ? p with { Notes = "新版知识", UpdatedAt = DateTimeOffset.Now } : p));
            automatic.IsChecked = true; session.ObserveConversation(new("shop", "买家 B", "资料更新"));
            await Task.Delay(650); Check(requests == 7, "knowledge changes alone do not trigger another automatic answer");
            await (Task)typeof(ReceptionSession).GetMethod("GenerateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
            await Until(() => editor.Text.Contains("建议8"));
            Check(requests == 8, "new knowledge versions do not reuse stale cached alternatives");
            products.SelectedItem = ((CatalogProduct[])products.ItemsSource).Single(p => p.ProductId == "333");
            await Task.Delay(650); Check(requests == 8, "unapproved products never start automatic generation");
            var failedMessage = Seed("买家 B", "失败问题", "111");
            await Until(() => service.Latest(failedMessage)?.KnowledgeCategory == "REPLY_UNAVAILABLE");
            Check(editor.Text.Length == 0 && !Field<TextBlock>(session, "_floatingStatus").Text.Contains("建议已生成"), "service failures leave reply blank and never show false generation success");
            for (var i = 0; i < 3; i++) session.ObserveConversation(new("shop", "买家 B", "刷新"));
            await Task.Delay(650); Check(requests == 10, "failed auto attempts retry once but do not loop on OCR refresh");
            automatic.IsChecked = false;
            var orphan = new InboxMessage("missing-session", Guid.NewGuid().ToString("N"), "shop", "买家 C", "上下文丢失", DateTime.Now.AddDays(1), "111", "fixture", false) { WindowScopeId = binding.ScopeId };
            session.AcceptMessage(orphan); session.ObserveConversation(new("shop", "买家 C", "测试上下文错误"));
            try { await (Task)typeof(ReceptionSession).GetMethod("GenerateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!; throw new Exception("Expected context failure"); }
            catch (InvalidOperationException) { }
            Check(Field<CancellationTokenSource?>(session, "_generationCancellation") is null && Field<Button>(session, "_generate").IsEnabled,
                "failure before task creation releases cancellation source and restores button");
            for (var i = 0; i < 3; i++)
            {
                products.SelectedItem = ((CatalogProduct[])products.ItemsSource).Single(p => p.ProductId == (i % 2 == 0 ? "222" : "111"));
                session.ObserveConversation(new("shop", "买家 C", "重复切换"));
            }
            Check(Field<CancellationTokenSource?>(session, "_generationCancellation") is null, "repeated cancellation after synchronous failure does not access a disposed source");
            var recovered = Seed("买家 D", "恢复后的问题", "111"); automatic.IsChecked = true;
            await Until(() => editor.Text.Contains("建议11"));
            Check(requests == 11 && service.Latest(recovered) is not null, "generation recovers after context failure and repeated switches");
            automatic.IsChecked = false;
            // Render the real options control, using only fixture data.
            var successful = service.Latest(recovered)!;
            typeof(ReceptionSession).GetMethod("ShowReply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object?[] { successful });
            var visual = (FrameworkElement)session.Panel.Content;
            visual.Measure(new Size(430, 1000)); visual.Arrange(new Rect(0, 0, 430, 1000)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(430, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(root, "single-reply.png"))) encoder.Save(file);
            Console.WriteLine($"Passed {passed} auto reply checks. Preview: {root}"); return 0;
        }
        finally
        {
            release.TrySetResult(); releaseEdit.TrySetResult(); await session.StopAsync(); listener.Stop(); await server; await Task.WhenAll(workers);
        }
    }
}
