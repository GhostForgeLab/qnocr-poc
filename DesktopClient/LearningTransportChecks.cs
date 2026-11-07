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
using System.Windows.Threading;
using QnocrPoc.Models;

namespace QNAgent;

internal static class LearningTransportChecks
{
    private sealed class RecordingJev : IJevGateway
    {
        public string Evidence = "", Verification = "";
        public Task<JevAssessment> AnalyzeAsync(ClientSettings settings, string evidence, CancellationToken token)
        { Evidence = evidence; return new FixtureJevGateway().AnalyzeAsync(settings, evidence, token); }
        public Task<JevVerification> VerifyAsync(ClientSettings settings, string evidence, string draft, string mode, CancellationToken token)
        { Verification = evidence; return new FixtureJevGateway().VerifyAsync(settings, evidence, draft, mode, token); }
    }
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(obj)!;
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
        int passed = 0; void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        var root = Path.GetFullPath(Path.Combine("Output", "LearningTransportChecks", Guid.NewGuid().ToString("N")));
        var shared = new ClientStore(root); shared.SaveProducts(new[] { new CatalogProduct { SellerId = "虚构店", ProductId = "123", Name = "合成收纳盒", Approved = true } });
        var product = shared.Products().Single();
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        shared.SaveSettings(new ClientSettings { SellerId = product.SellerId, AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "fixture" });
        int replyCalls = 0, learningCalls = 0; string lastReplyPayload = "", systemPrompt = "";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await listener.GetContextAsync(); using var reader = new StreamReader(request.Request.InputStream);
                    using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                    var context = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                    using var parsed = JsonDocument.Parse(context); string content;
                    if (parsed.RootElement.TryGetProperty("HumanReply", out _))
                    {
                        Interlocked.Increment(ref learningCalls); systemPrompt = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                        content = JsonSerializer.Serialize(new { kind = "Knowledge", proposal = "可用干软布轻擦。", reason = "人工补充清洁方法。", evidence = "可用干软布轻擦。" });
                    }
                    else { var number = Interlocked.Increment(ref replyCalls); lastReplyPayload = context; content = "清洁方法需要核实。";
                        if (number == 4) { held.SetResult(); await release.Task; } }
                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
                    await request.Response.OutputStream.WriteAsync(body); request.Response.Close();
                }
            }
            catch (Exception e) when (!listener.IsListening && e is HttpListenerException or ObjectDisposedException) { }
        });
        var target = new QianniuWindow("fixture", Environment.ProcessId, 100, 100, "学习测试窗口", new System.Drawing.Rectangle(0, 0, 800, 600), true, false, false);
        var registry = new WindowBindingRegistry(shared); var binding = registry.GetOrCreate(target, product.ShopId, "fixture");
        var jev = new RecordingJev(); var session = new ReceptionSession(shared, registry.OpenStore(binding), binding, target, new ResourceDictionary(), _ => { }, _ => { }, jev, autoGenerate: false);
        try
        {
            var q = new InboxMessage("s", "q", product.SellerId, "虚构买家", "如何清洁？", DateTime.Now, product.ProductId, "capture", false) { WindowScopeId = binding.ScopeId, Sequence = 1 };
            session.AcceptMessage(q); session.ObserveConversation(new(product.SellerId, q.Buyer, "当前买家"));
            Field<ComboBox>(session, "_replyProduct").SelectedItem = product;
            var context = new SessionContextView(q.SessionId, "synthetic-id", product.ProductId, new[] { new EnvelopeMessage("buyer", q.Text, q.Timestamp) }, DateTimeOffset.Now, q.CaptureId, q.MessageId, "request", 1);
            var replies = Field<ReplyService>(session, "_replies"); await replies.GenerateAsync(q, context, product, shared.Settings(), CancellationToken.None);
            Check(JsonDocument.Parse(jev.Evidence).RootElement.GetProperty("Product").GetProperty("ConfirmedLearning").GetArrayLength() == 0, "initial JEV request contains no unconfirmed memory");
            var editor = Field<TextBox>(session, "_reply"); editor.Text = "保留这份正在编辑的草稿";
            var seller = q with { MessageId = "seller", Role = "seller", Text = "可用干软布轻擦。", Timestamp = DateTime.Now.AddSeconds(1), Sequence = 2 };
            session.AcceptMessage(seller);
            Check(shared.Business.Read().Candidates.Length == 1 && editor.Text == "保留这份正在编辑的草稿", "actual seller-message hook collects sample without replacing draft");
            using var timeout = new CancellationTokenSource(15000);
            while (shared.Business.Read().Candidates.Single().Status == "PendingAnalysis") await Task.Delay(50, timeout.Token);
            var candidate = shared.Business.Read().Candidates.Single();
            Check(candidate.Status == "Pending" && learningCalls == 1 && replyCalls == 1, "debounced seller update invokes learning analysis, not another buyer reply");
            Check(systemPrompt.Contains("不自动生效") && systemPrompt.Contains("连续原文"), "learning HTTP request carries dedicated no-invention review instructions");
            new LearningService(shared).Confirm(candidate.Id, candidate.Question, candidate.Proposal);
            await replies.GenerateAsync(q, context, product, shared.Settings(), CancellationToken.None);
            bool HasRule(string input) => JsonDocument.Parse(input).RootElement.GetProperty("Product").GetProperty("ConfirmedLearning")[0].GetProperty("Content").GetString() == seller.Text;
            Check(HasRule(jev.Evidence) && HasRule(jev.Verification) && HasRule(lastReplyPayload), "confirmed experience reaches JEV analysis, configured reply model and JEV verification");
            Check(!lastReplyPayload.Contains("AiReply") && !lastReplyPayload.Contains("HumanReply"), "reply generation receives only confirmed rule, not raw learning samples");
            new LearningService(shared).SetActive(shared.Business.Read().Rules.Single().Id, false);
            await replies.GenerateAsync(q, context, product, shared.Settings(), CancellationToken.None);
            Check(JsonDocument.Parse(lastReplyPayload).RootElement.GetProperty("Product").GetProperty("ConfirmedLearning").GetArrayLength() == 0, "disabled rule is removed from next real request payload");
            Check(jev.Evidence == jev.Verification, "verification receives the same scoped evidence as intention analysis");
            var learning = new LearningService(shared); var ruleId = shared.Business.Read().Rules.Single().Id;
            learning.SetActive(ruleId, true);
            var before = replies.Latest(q)!.ReplyPreviewId;
            var delayed = replies.GenerateAsync(q, context, product, shared.Settings(), CancellationToken.None);
            await held.Task; learning.SetActive(ruleId, false); release.SetResult();
            bool rejected = false; try { await delayed; } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && replies.Latest(q)!.ReplyPreviewId == before, "disabling experience mid-request discards stale result before saving a preview");
        }
        finally { await session.StopAsync(); listener.Stop(); await server; }
        Console.WriteLine($"Passed {passed} learning integration checks (local HTTP and synthetic data only)."); return 0;
    }
    internal static async Task<int> LiveAsync(string root)
    {
        var settings = new ClientStore(Path.Combine(root, "Output", "DesktopClient")).Settings();
        var candidate = new LearningCandidate { Question = "这个演示收纳盒怎样清洁？", AiReply = "清洁方法需要核实。", HumanReply = "可用干软布轻擦。不要用硬刷。" };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var raw = await new LearningClient().AnalyzeAsync(settings, JsonSerializer.Serialize(new { candidate.Question, candidate.AiReply, candidate.HumanReply, Product = new { Name = "虚构演示收纳盒", Material = "测试合金" } }), deadline.Token);
        var result = LearningService.Parse(raw, candidate);
        if (result.Kind != "Knowledge") throw new InvalidOperationException("合成样本没有被归为可复用知识，需检查分析。");
        Console.WriteLine("PASS: configured model produced a grounded learning proposal from synthetic data; no real product/chat data or knowledge writes.");
        Console.WriteLine("Proposal: " + result.Proposal); return 0;
    }
}
