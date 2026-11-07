using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

namespace QNAgent;

internal static class SessionRecoveryChecks
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero); private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan time) { _now += time; _ticks += time.Ticks; }
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
        int count = 0; void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
        var root = Path.GetFullPath(Path.Combine("Output", "SessionRecoveryChecks", Guid.NewGuid().ToString("N")));
        var shared = new ClientStore(root); var registry = new WindowBindingRegistry(shared); var clock = new Clock();
        shared.SaveProducts(new[] { new CatalogProduct { SellerId = "shop", ProductId = "111", Name = "收纳盒", Approved = true } });
        var target = new QianniuWindow("fixture", Environment.ProcessId, 100, 100, "fixture", new System.Drawing.Rectangle(0, 0, 800, 640), true, false, false);
        var binding = registry.GetOrCreate(target, "shop", "fixture"); var state = registry.OpenStore(binding);
        var session = new ReceptionSession(shared, state, binding, target, new ResourceDictionary(), _ => { }, _ => { }, new FixtureJevGateway(), false, clock);
        var connection = Field<QianniuConnection>(session, "_connection"); var pipeline = Field<AgentContextPipeline>(connection, "_context");
        try
        {
            var earlier = new EnvelopeMessage("buyer", "旧会话里的一条历史", new DateTime(2026, 10, 23, 10, 0, 0));
            var latest = new EnvelopeMessage("buyer", "这是一条还是一盒？", earlier.Timestamp.AddMinutes(1));
            var old = pipeline.ProcessMessage(new MessageEnvelope("Qianniu.OCR", "shop", "buyer-a", "", new[] { earlier, latest }));
            var item = old.Messages.Last();
            var message = new InboxMessage(old.SessionId, item.MessageId, "shop", "buyer-a", latest.Text, latest.Timestamp, "111", "fixture", true) { WindowScopeId = binding.ScopeId };
            session.AcceptMessage(message); session.ObserveConversation(new("shop", "buyer-a", "当前画面"));
            var service = Field<ReplyService>(session, "_replies"); var oldId = Guid.NewGuid().ToString("N");
            var preview = new ReplyPreview(message.SessionId, "buyer-a", connection.Context(message) with { ProductId = "111" }, "旧会话建议", null, DateTimeOffset.Now, "Shadow", "fixture", "old", "old", oldId);
            service.Assistant.InitializePreview(Path.Combine(state.ReviewRoot, "outbox", oldId), preview);
            state.Write(Path.Combine("reviews", "outbox", oldId, "reply-preview.json"), preview);
            state.Write(Path.Combine("reply-index", message.MessageId.Replace(':', '_') + ".json"), new { PreviewId = oldId });
            clock.Advance(TimeSpan.FromMinutes(31));
            Check(pipeline.GetSession(old.SessionId)!.Status == AgentSessionStatus.Inactive, "fixture reproduces idle-expired desktop session");
            Reject(() => connection.Context(message), "strict intent adapter continues rejecting inactive sessions");
            var prepared = connection.PrepareCurrentReply(message, "shop", "buyer-a");
            Check(prepared.Message.SessionId != old.SessionId && prepared.Message.MessageId == message.MessageId && prepared.Context.SessionId == prepared.Message.SessionId,
                "current visible message is rebound to a new active session with its stable message ID");
            Check(prepared.Context.Messages.Count == 1 && prepared.Context.Messages[0].Text == latest.Text && pipeline.GetSession(old.SessionId)!.Status == AgentSessionStatus.Inactive,
                "expired history stays inactive and is not copied into the recovered context");
            Check(service.Latest(prepared.Message) is null && service.Options(prepared.Message).Length == 0 && service.Assistant.Show(oldId) is not null,
                "old reply preview remains historical and cannot leak into the recovered session");
            session.AcceptMessage(prepared.Message); session.ObserveConversation(new("shop", "buyer-a", "新会话绑定"));
            Check(Field<InboxMessage>(session, "_selected").SessionId == prepared.Message.SessionId && state.Messages().Single().SessionId == prepared.Message.SessionId,
                "same-message rebind updates both UI selection and persisted inbox rather than being deduplicated away");
            var repeat = connection.PrepareCurrentReply(message, "shop", "buyer-a");
            Check(repeat.Message.SessionId == prepared.Message.SessionId && repeat.Context.Messages.Count == 1, "recovery reuses current active session without duplicate messages");
            Reject(() => connection.PrepareCurrentReply(message, "shop", "buyer-b"), "recovery rejects a different observed buyer");
            Reject(() => connection.PrepareCurrentReply(message with { SellerId = "other" }, "other", "buyer-a"), "recovery rejects a different seller identity");
            Reject(() => connection.PrepareCurrentReply(message with { WindowScopeId = "other" }, "shop", "buyer-a"), "recovery rejects a different physical window");
            Reject(() => connection.PrepareCurrentReply(message with { Text = "另一条消息" }, "shop", "buyer-a"), "recovery rejects altered message contents");
            clock.Advance(TimeSpan.FromMinutes(31));
            var uiPrepared = (QianniuConnection.PreparedReply)typeof(ReceptionSession).GetMethod("PrepareSelectedContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
            Check(Field<InboxMessage>(session, "_selected").SessionId == uiPrepared.Context.SessionId && uiPrepared.Context.SessionId != prepared.Context.SessionId,
                "manual generation preparation adopts a refreshed session before building model input");
            clock.Advance(TimeSpan.FromMinutes(31));
            var automatic = Field<CheckBox>(session, "_automatic"); automatic.IsChecked = true; automatic.IsChecked = false;
            var autoRebound = Field<InboxMessage>(session, "_selected");
            Check(autoRebound.SessionId == uiPrepared.Message.SessionId,
                "loading expired historical messages does not trigger an automatic reply");
            _ = (QianniuConnection.PreparedReply)typeof(ReceptionSession).GetMethod("PrepareSelectedContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;
            int attempts = 0;
            await session.RecognizeProductWithRetryAsync((_, _) => ++attempts == 1
                ? Task.FromException<string?>(new ConversationChangedException("fixture repaint")) : Task.FromResult<string?>("111"));
            Check(attempts == 2 && ((CatalogProduct)Field<ComboBox>(session, "_replyProduct").SelectedItem).ProductId == "111",
                "transient product recognition change retries once and preserves the valid selection");
            attempts = 0;
            await session.RecognizeProductWithRetryAsync((_, _) =>
            {
                attempts++; session.ObserveConversation(new("shop", "buyer-b", "切换买家")); return Task.FromResult<string?>("111");
            });
            Check(attempts == 1 && Field<ComboBox>(session, "_replyProduct").SelectedItem is null, "product result cannot cross into a switched buyer");
        }
        finally { await session.StopAsync(); }
        using (var restarted = new QianniuConnection(state, binding.ScopeId, clock))
        {
            var saved = state.Messages().Single();
            var restored = restarted.PrepareCurrentReply(saved, "shop", "buyer-a");
            Check(restored.Context.SessionId == saved.SessionId, "restarted desktop uses the updated persisted message binding");
        }
        Console.WriteLine($"Passed {count} session recovery checks. Evidence: {root}"); return 0;
    }
    internal static int ReplaySavedWindows(string root)
    {
        var output = Path.Combine(root, "Output", "SessionRecoveryReplay", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output); int count = 0;
        foreach (var folder in Directory.GetDirectories(Path.Combine(root, "Output", "DesktopClient", "windows")))
        {
            var database = Path.Combine(folder, "context.db"); var inbox = Path.Combine(folder, "inbox.json");
            if (!File.Exists(database) || !File.Exists(inbox)) continue;
            var records = System.Text.Json.JsonSerializer.Deserialize<InboxMessage[]>(File.ReadAllText(inbox), ClientStore.Json) ?? Array.Empty<InboxMessage>();
            var selected = records.Where(m => m.Buyer is "tb300116357" or "oswaldsterling").GroupBy(m => m.Buyer).Select(g => g.OrderByDescending(m => m.Timestamp).First()).ToArray();
            if (selected.Length == 0) continue;
            var scope = Path.GetFileName(folder); var copy = new ClientStore(Path.Combine(output, scope));
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(copy.Root, "context.db") }.ToString()))
            { source.Open(); destination.Open(); source.BackupDatabase(destination); }
            using var connection = new QianniuConnection(copy, scope);
            foreach (var message in selected)
            {
                var before = Field<AgentContextPipeline>(connection, "_context").GetSession(message.SessionId);
                var prepared = connection.PrepareCurrentReply(message, message.SellerId, message.Buyer);
                if (prepared.Context.SessionId != prepared.Message.SessionId || prepared.Context.Messages.Last().Text != message.Text)
                    throw new Exception("Saved-window replay did not retain selected message.");
                Console.WriteLine($"PASS: saved buyer context recovered; previous={before?.Status}, rebound={message.SessionId != prepared.Message.SessionId}, messages={prepared.Context.Messages.Count}"); count++;
            }
        }
        if (count < 2) throw new Exception("Both reported buyer records were not found for replay.");
        Console.WriteLine($"Replayed {count} real inbox bindings on database copies only. No external models called. Evidence: {output}"); return 0;
    }
}
