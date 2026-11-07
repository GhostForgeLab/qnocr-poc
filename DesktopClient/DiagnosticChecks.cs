using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QNAgent;

internal static class DiagnosticChecks
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    internal static int Run()
    {
        int count = 0; void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var root = Path.GetFullPath(Path.Combine("Output", "DiagnosticChecks", Guid.NewGuid().ToString("N")));
        var clock = new Clock(); var levels = new DiagnosticLog(Path.Combine(root, "levels"), clock);
        levels.Configure(DiagnosticLogLevel.Warning);
        levels.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed);
        levels.Write(DiagnosticLogLevel.Warning, DiagnosticEvent.ReplyFinished);
        levels.Write(DiagnosticLogLevel.Information, DiagnosticEvent.AppStarted);
        levels.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.MessageObserved);
        var chosen = levels.Snapshot(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, out _);
        Check(chosen.Length == 2 && chosen.All(e => e.Level <= DiagnosticLogLevel.Warning), "log level includes only selected severity and higher");
        var rotating = new DiagnosticLog(Path.Combine(root, "rotation"), clock, 400, 3); rotating.Configure(DiagnosticLogLevel.Debug);
        for (var i = 0; i < 35; i++) rotating.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.MessageObserved, new() { Count = i });
        Check(rotating.LogFiles().Length <= 3 && rotating.Snapshot(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, out _).Last().Count == 34,
            "rotation bounds disk segments while retaining newest events");
        clock.Now = clock.Now.AddDays(8); rotating.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed);
        Check(rotating.LogFiles().Length == 1, "expired log segments are removed after seven-day retention");
        var concurrent = new DiagnosticLog(Path.Combine(root, "concurrent")); concurrent.Configure(DiagnosticLogLevel.Debug);
        Parallel.For(0, 80, i => concurrent.Write(DiagnosticLogLevel.Debug, DiagnosticEvent.MessageObserved, new() { Count = i }));
        Check(concurrent.Snapshot(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, out var badLines).Length == 80 && badLines == 0,
            "concurrent event writes remain complete JSON lines");
        var store = new ClientStore(Path.Combine(root, "private-source"));
        const string secret = "sk-private-key-CANARY-92831";
        const string encrypted = "DPAPI-CIPHERTEXT-CANARY-88391";
        const string chat = "PRIVATE_CHAT_CANARY_卖家买家正文";
        const string identity = "PRIVATE_BUYER_AND_SHOP_CANARY";
        const string endpoint = "https://private-host.example/private-path-canary?key=URL_KEY_CANARY";
        store.SaveSettings(new ClientSettings { SellerId = identity, AiSecret = secret, JevSecret = encrypted, OnlineKnowledgeSecret = secret,
            AiEndpoint = endpoint, OnlineKnowledgeUrl = endpoint, AiModel = "PRIVATE_MODEL_CANARY", LogLevel = DiagnosticLogLevel.Debug });
        var logger = store.Diagnostics;
        try { throw new InvalidOperationException(secret + " " + chat + " " + endpoint); }
        catch (Exception error) { logger.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed,
            new() { WindowId = identity, SessionId = chat, ModelId = secret, Mode = chat, Code = secret, Role = identity }, error); }
        logger.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ReplyFinished, new() { Mode = "ANSWER", ModelId = "deepseek/test", DurationMs = 1234, HttpStatus = 200, Success = true });
        var recent = logger.Snapshot(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, out _);
        Check(recent.First().ErrorKind == "InvalidOperationException" && recent.First().Stack.Length > 0 && recent.Last().DurationMs == 1234,
            "errors keep type and project stack locations without their sensitive messages");
        var scope = new string('b', 32); var preview = new string('a', 32); var oldPreview = new string('c', 32);
        var auditPath = Path.Combine("windows", scope, "reviews", "outbox", preview, "reply-assessment.json");
        store.Write(auditPath, new
        {
            Mode = "ANSWER", ModelUsed = true, RejectedDraft = chat, Buyer = identity,
            Calls = new { StartedAt = DateTimeOffset.UtcNow, ActualReplyModel = "PRIVATE_MODEL_CANARY", ReplyRequestsAttempted = 2,
                LastHttpStatus = 200, ElapsedMilliseconds = 2400, ContextMessageCount = 10, ContextSellerMessages = 4,
                ReplyFailureMessage = secret, FullUrl = endpoint }
        });
        store.Write(Path.Combine("windows", scope, "reviews", "outbox", oldPreview, "reply-assessment.json"), new
            { Mode = "HUMAN_CHECK", Calls = new { StartedAt = DateTimeOffset.UtcNow.AddDays(-4) } });
        store.Write(Path.Combine("windows", scope, "reviews", "outbox", new string('d', 32), "reply-assessment.json"), new
            { Mode = "JEV_UNAVAILABLE", ModelUsed = false, Calls = new { StartedAt = DateTimeOffset.UtcNow, LastHttpStatus = (int?)null, ReplyRequestsAttempted = 0 } });
        store.Write("products.json", new[] { new { SellerId = identity, Notes = chat, Token = secret } });
        store.Write(Path.Combine("windows", scope, "inbox.json"), new[] { new { Text = chat, Buyer = identity } });
        File.WriteAllText(Path.Combine(store.Root, "context.db"), "DATABASE_CANARY");
        File.WriteAllText(Path.Combine(store.Root, "screenshot.png"), "SCREENSHOT_CANARY");
        // Even future/foreign JSON fields and malformed source lines must not enter the zip verbatim.
        File.AppendAllText(logger.LogFiles().Last(), "\nnot-json:" + secret + "\n" +
            JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow, Level = "Information", Event = "AppStarted", message = chat, ApiKey = secret }) + "\n");
        var zipPath = Path.Combine(root, "diagnostic.zip"); var result = DiagnosticExporter.Export(store, zipPath, DateTimeOffset.UtcNow.AddHours(-24));
        string contents;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            Check(archive.Entries.Select(e => e.FullName).OrderBy(n => n).SequenceEqual(new[] { "README.txt", "calls.jsonl", "events.jsonl", "manifest.json" }.OrderBy(n => n)),
                "zip contains only the four allowlisted diagnostic artifacts");
            contents = string.Join("\n", archive.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()); using var manifest = JsonDocument.Parse(reader.ReadToEnd());
            Check(manifest.RootElement.GetProperty("Application").GetProperty("SHA256").GetString()!.Length == 64 &&
                manifest.RootElement.GetProperty("Settings").GetProperty("ReplyKeyConfigured").GetBoolean(), "export identifies the exact build and key-presence status without exporting credentials");
        }
        Check(new[] { secret, encrypted, chat, identity, endpoint, "private-host.example", "PRIVATE_MODEL_CANARY", "DATABASE_CANARY", "SCREENSHOT_CANARY", root }.All(v => !contents.Contains(v, StringComparison.Ordinal)),
            "export excludes secrets, encrypted values, identities, chat/replies, full URLs, source paths and data files");
        Check(result.Calls == 2 && result.Skipped > 0 && contents.Contains("2400") && contents.Contains("JEV_UNAVAILABLE"), "24-hour export keeps failed calls with null metrics and reports unreadable records");
        var sevenDays = DiagnosticExporter.Export(store, Path.Combine(root, "seven-days.zip"), DateTimeOffset.UtcNow.AddDays(-7));
        Check(sevenDays.Calls == 3, "wider time range includes older retained call summaries");
        var existingPath = Path.Combine(root, "cancelled.zip"); File.WriteAllText(existingPath, "existing archive");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { DiagnosticExporter.Export(store, existingPath, DateTimeOffset.MinValue, cancelled.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { Check(File.ReadAllText(existingPath) == "existing archive", "cancelled export does not overwrite an existing destination"); }
        File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{broken-settings");
        var startupPath = Path.Combine(root, "startup-failure.zip"); DiagnosticExporter.Export(store, startupPath, DateTimeOffset.MinValue);
        using (var archive = ZipFile.OpenRead(startupPath))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open()))
        using (var json = JsonDocument.Parse(reader.ReadToEnd()))
            Check(json.RootElement.GetProperty("Settings").ValueKind == JsonValueKind.Null, "diagnostics can still export when settings cannot be loaded for startup");
        var scopeStore = new WindowBindingRegistry(store).OpenStore(new WindowBinding(scope, "fixture", identity, "fixture"));
        Check(ReferenceEquals(scopeStore.Diagnostics, logger), "all window logs share the configured root logger and level");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var uiStore = new ClientStore(Path.Combine(root, "ui")); uiStore.SaveSettings(new ClientSettings { AiModel = "keep-this-model" });
        var window = new MainWindow(uiStore);
        try
        {
            var combo = Field<ComboBox>(window, "_logLevel"); combo.SelectedItem = combo.Items.Cast<object>().Single(v => v.ToString() == "调试");
            Check(uiStore.Settings().LogLevel == DiagnosticLogLevel.Debug && uiStore.Diagnostics.Level == DiagnosticLogLevel.Debug && uiStore.Settings().AiModel == "keep-this-model",
                "log-level UI applies immediately and persists without altering model settings");
            var page = (ScrollViewer)Field<UIElement>(window, "_diagnosticsPage");
            Field<ContentControl>(window, "_page").Content = page; page.Background = window.Background;
            page.Measure(new Size(1000, 800)); page.Arrange(new Rect(0, 0, 1000, 800)); page.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1000, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, "diagnostic-page.png")); encoder.Save(output);
        }
        finally { window.Close(); app.Shutdown(); }
        Console.WriteLine($"Passed {count} diagnostic checks. Evidence: {root}"); return 0;
    }
}
