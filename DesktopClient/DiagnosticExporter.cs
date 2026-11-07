using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace QNAgent;

internal sealed record DiagnosticExportResult(string Path, int Events, int Calls, int Skipped);
internal static class DiagnosticExporter
{
    internal static DiagnosticExportResult Export(ClientStore store, string destination, DateTimeOffset since, CancellationToken token = default)
    {
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 ZIP 文件作为导出位置。");
        var path = Path.GetFullPath(destination); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var log = store.Diagnostics; var until = DateTimeOffset.UtcNow;
        var events = log.Snapshot(since, until, out var skipped); var summaries = Summaries(store, since, until, token, ref skipped);
        ClientSettings? settings = null;
        try { settings = store.Settings(); } catch (Exception e) when (e is IOException or JsonException) { skipped++; }
        var manifest = new
        {
            Schema = 1, ExportedAt = until, From = since, Until = until,
            Application = Build(typeof(Program).Assembly), Core = Build(typeof(QnocrPoc.Models.IntentResult).Assembly),
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LocalUtcOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).ToString(),
            LogLevel = log.Level.ToString(), LogWriteError = log.LastWriteError,
            Settings = settings is null ? null : new
            {
                settings.CaptureIntervalMilliseconds, settings.FastRecognition,
                ReplyProvider = Provider(settings.AiEndpoint), ReplyModelFamily = DiagnosticLog.ModelFamily(settings.AiModel),
                ReplyModelRef = log.Reference("model", settings.AiModel), ReplyKeyConfigured = !string.IsNullOrEmpty(settings.AiSecret),
                JevProvider = Provider(settings.JevEndpoint), JevModelRef = log.Reference("model", settings.JevModel),
                JevKeyConfigured = !string.IsNullOrEmpty(settings.JevSecret) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")),
                OnlineKnowledgeConfigured = !string.IsNullOrEmpty(settings.OnlineKnowledgeUrl)
            },
            EventCount = events.Length, CallSummaryCount = summaries.Count, SkippedOrTruncated = skipped,
            Privacy = "No API keys, encrypted secrets, chat text, model replies, screenshots, account names, full URLs, product content or databases. Correlation IDs are salted hashes."
        };
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                void Write(string name, string text)
                { token.ThrowIfCancellationRequested(); using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
                Write("manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                Write("events.jsonl", string.Join("\n", events.Select(e => JsonSerializer.Serialize(DiagnosticLog.Clean(e), DiagnosticLog.Json))));
                Write("calls.jsonl", string.Join("\n", summaries.Select(e => JsonSerializer.Serialize(DiagnosticLog.Clean(e), DiagnosticLog.Json))));
                Write("README.txt", "QN Agent 诊断包\n\nmanifest.json：程序构建指纹、运行环境和不含密钥的配置状态。\nevents.jsonl：所选时间内的结构化日志，按当前日志级别记录。\ncalls.jsonl：已有回复记录的技术摘要，可能包含人工修订关联，不能直接作为 API 计费次数。\n\n不包含密钥（包括加密值）、聊天正文、模型回复、截图、店铺/买家名称、商品资料或数据库。\n时间戳带时区；标识已转换成散列，只用于关联同一次问题。最多保留最近 10000 条事件和 1000 条调用摘要；省略数量见 manifest。\n日志级别较低时，过去未记录的调试事件无法补回。\n\n请随文件说明问题发生的大概时间和操作步骤。\n");
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        log.Write(DiagnosticLogLevel.Information, DiagnosticEvent.ExportCreated, new() { Count = events.Length + summaries.Count, Success = true });
        return new(path, events.Length, summaries.Count, skipped);
    }
    private static object Build(Assembly assembly)
    {
        string? hash = null;
        if (File.Exists(assembly.Location)) { using var file = File.OpenRead(assembly.Location); hash = Convert.ToHexString(SHA256.HashData(file)); }
        return new { Name = assembly.GetName().Name, Version = assembly.GetName().Version?.ToString(), SHA256 = hash };
    }
    private static string Provider(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri.IsLoopback ? "Local" : uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ? "OpenRouter"
            : uri.Host.Equals("api.typesafe.ai", StringComparison.OrdinalIgnoreCase) ? "TypeSafe" : "CustomRemote" : "NotConfigured";
    private static bool PlainPath(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static List<DiagnosticEntry> Summaries(ClientStore store, DateTimeOffset since, DateTimeOffset until, CancellationToken token, ref int skipped)
    {
        var result = new List<DiagnosticEntry>(); var sources = new List<(string File, string? Scope)>();
        void Collect(string outbox, string? scope)
        {
            if (!Directory.Exists(outbox) || !PlainPath(outbox)) return;
            foreach (var folder in Directory.EnumerateDirectories(outbox))
            {
                token.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-fA-F0-9]{32}$") || !PlainPath(folder)) continue;
                var file = Path.Combine(folder, "reply-assessment.json");
                if (File.Exists(file) && PlainPath(file) && new DateTimeOffset(File.GetLastWriteTimeUtc(file)) >= since) sources.Add((file, scope));
            }
        }
        var rootReviews = Path.Combine(store.Root, "reviews");
        if (Directory.Exists(rootReviews) && PlainPath(rootReviews)) Collect(Path.Combine(rootReviews, "outbox"), null);
        var windows = Path.Combine(store.Root, "windows");
        if (Directory.Exists(windows) && PlainPath(windows))
            foreach (var window in Directory.EnumerateDirectories(windows).Where(p => Regex.IsMatch(Path.GetFileName(p), "^[a-fA-F0-9]{32}$") && PlainPath(p)))
            {
                var reviews = Path.Combine(window, "reviews");
                if (Directory.Exists(reviews) && PlainPath(reviews)) Collect(Path.Combine(reviews, "outbox"), Path.GetFileName(window));
            }
        var selected = sources.OrderByDescending(s => File.GetLastWriteTimeUtc(s.File)).Take(1000).ToArray(); skipped += Math.Max(0, sources.Count - selected.Length);
        foreach (var source in selected)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(source.File).Length > 256 * 1024) { skipped++; continue; }
                using var json = JsonDocument.Parse(File.ReadAllText(source.File)); var root = json.RootElement;
                var calls = root.TryGetProperty("Calls", out var node) && node.ValueKind == JsonValueKind.Object ? node : default;
                string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                int? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
                bool? Boolean(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
                JsonElement Child(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;
                double? Probability(JsonElement item) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Probability", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var p) && double.IsFinite(p) && p is >= 0 and <= 1 ? p : null;
                var assessment = Child(root, "Assessment");
                var at = DateTimeOffset.TryParse(Text(calls, "StartedAt"), out var timestamp) ? timestamp : new DateTimeOffset(File.GetLastWriteTimeUtc(source.File));
                if (at < since || at > until) continue;
                var model = Text(calls, "ActualReplyModel") ?? Text(calls, "ReplyModel");
                var mode = DiagnosticLog.SafeMode(Text(root, "Mode"));
                result.Add(new DiagnosticEntry
                {
                    Timestamp = at, Event = DiagnosticEvent.ReplyFinished, Level = mode is "JEV_UNAVAILABLE" or "REPLY_UNAVAILABLE" or "VERIFY_UNAVAILABLE" ? DiagnosticLogLevel.Error : DiagnosticLogLevel.Information,
                    WindowRef = store.Diagnostics.Reference("window", source.Scope), PreviewRef = store.Diagnostics.Reference("preview", new DirectoryInfo(Path.GetDirectoryName(source.File)!).Name),
                    ModelRef = store.Diagnostics.Reference("model", model), ModelFamily = DiagnosticLog.ModelFamily(model), Mode = mode,
                    Code = DiagnosticLog.SafeCode(Text(calls, "ReplyFailureCode") ?? Text(root, "Failure")),
                    Intent = Text(Child(assessment, "Intent"), "IntentName"), Purpose = Text(Child(assessment, "Purpose"), "Name"),
                    CoverageProbability = Probability(Child(assessment, "Coverage")), SupportProbability = Probability(Child(Child(root, "Verification"), "Support")),
                    HttpStatus = Number(calls, "LastHttpStatus"), Count = Number(calls, "ReplyRequestsAttempted"), DurationMs = Number(calls, "ElapsedMilliseconds"),
                    ContextMessages = Number(calls, "ContextMessageCount"), SellerMessages = Number(calls, "ContextSellerMessages"), Success = Boolean(root, "ModelUsed")
                });
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { skipped++; }
        }
        return result.OrderBy(e => e.Timestamp).ToList();
    }
}
