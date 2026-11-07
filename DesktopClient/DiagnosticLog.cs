using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QNAgent;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticLogLevel { Error, Warning, Information, Debug }
internal enum DiagnosticEvent
{
    AppStarted, AppStopped, UnhandledError, UiOperationFailed, WindowConnected, WindowDisconnected,
    CaptureFailed, RecognitionHeld, PreviewFailed, MessageObserved, ConversationConfirmed, ConversationPending, ContextUnavailable, SessionRecovered,
    ReplyStarted, ReplyFinished, ReplyCancelled, ReplyCopied, ReplyRetried, AdviceStarted, AdviceFinished, AdviceFailed,
    ProductSaved, ProductsImported, LogLevelChanged, ExportCreated,
    LearningCollected, LearningAnalyzed, LearningFailed, LearningConfirmed, BusinessRestored
}
internal sealed record DiagnosticFields
{
    public string? WindowId { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? PreviewId { get; init; }
    public string? ModelId { get; init; }
    public string? Mode { get; init; }
    public string? Code { get; init; }
    public string? Role { get; init; }
    public string? Intent { get; init; }
    public string? Purpose { get; init; }
    public double? CoverageProbability { get; init; }
    public double? SupportProbability { get; init; }
    public bool? Success { get; init; }
    public int? Count { get; init; }
    public int? HttpStatus { get; init; }
    public long? DurationMs { get; init; }
    public int? ContextMessages { get; init; }
    public int? SellerMessages { get; init; }
}
internal sealed record DiagnosticFrame(string Method, string? File, int Line);
internal sealed record DiagnosticEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public DiagnosticLogLevel Level { get; init; }
    public DiagnosticEvent Event { get; init; }
    public string? RunId { get; init; }
    public string? WindowRef { get; init; }
    public string? SessionRef { get; init; }
    public string? MessageRef { get; init; }
    public string? PreviewRef { get; init; }
    public string? ModelRef { get; init; }
    public string? ModelFamily { get; init; }
    public string? Mode { get; init; }
    public string? Code { get; init; }
    public string? Role { get; init; }
    public string? Intent { get; init; }
    public string? Purpose { get; init; }
    public double? CoverageProbability { get; init; }
    public double? SupportProbability { get; init; }
    public bool? Success { get; init; }
    public int? Count { get; init; }
    public int? HttpStatus { get; init; }
    public long? DurationMs { get; init; }
    public int? ContextMessages { get; init; }
    public int? SellerMessages { get; init; }
    public string? ErrorKind { get; init; }
    public int? HResult { get; init; }
    public DiagnosticFrame[] Stack { get; init; } = Array.Empty<DiagnosticFrame>();
}

// No free-text message/exception/payload field: accidentally passing a chat or a key cannot
// turn it into a log message. Only fixed events, bounded metadata and salted references persist.
internal sealed class DiagnosticLog
{
    private static readonly ConcurrentDictionary<string, DiagnosticLog> Instances = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    { "ANSWER", "ANSWER_OPTIONS", "EVIDENCE_FIRST", "IDENTITY_CLARIFICATION", "SOCIAL_REPLY", "PARTIAL", "CLARIFY_BUYER", "HUMAN_CHECK", "DIRECT_FACTS", "REVIEW_DRAFT", "VISUAL_CLARIFICATION", "JEV_UNAVAILABLE", "REPLY_UNAVAILABLE", "VERIFY_UNAVAILABLE", "UNSUPPORTED_REPLY", "STYLE_REJECTED" };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    { "OutputTruncated", "EmptyReply", "InvalidJson", "InvalidResponse", "ContentFiltered", "ReplyTooLong", "Timeout", "NetworkFailure", "STYLE_REJECTED", "VERIFY_UNAVAILABLE", "UNSUPPORTED_REPLY", "Other", "ApiKeyMissing",
      "InvalidDataException", "InvalidOperationException", "JsonException", "HttpRequestException", "TaskCanceledException", "OperationCanceledException", "ObjectDisposedException", "CryptographicException", "KeyNotFoundException", "BodyChange", "IdentityChange", "TimestampHeaderMissing", "NoUniqueBuyer", "FullChatOcrFallback" };
    private readonly object _gate = new();
    private readonly byte[] _salt;
    private readonly TimeProvider _clock;
    private readonly int _maxFileBytes, _maxFiles;
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private string? _currentFile;
    private DateTimeOffset _lastPrune;
    private DiagnosticLogLevel _level = DiagnosticLogLevel.Information;
    public string Root { get; }
    public string DirectoryPath { get; }
    public string? LastWriteError { get; private set; }
    public DiagnosticLogLevel Level { get { lock (_gate) return _level; } }
    internal static DiagnosticLog ForRoot(string root) => Instances.GetOrAdd(Path.GetFullPath(root), path => new(path));
    internal DiagnosticLog(string root, TimeProvider? clock = null, int maxFileBytes = 2 * 1024 * 1024, int maxFiles = 10)
    {
        Root = Path.GetFullPath(root); DirectoryPath = Path.Combine(Root, "diagnostics"); _clock = clock ?? TimeProvider.System;
        _maxFileBytes = Math.Max(256, maxFileBytes); _maxFiles = Math.Clamp(maxFiles, 1, 30); _salt = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(DirectoryPath); var key = Path.Combine(DirectoryPath, "reference-salt.bin");
            if (File.Exists(key) && new FileInfo(key).Length == 32) _salt = File.ReadAllBytes(key);
            else File.WriteAllBytes(key, _salt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastWriteError = ErrorName(e); }
    }
    internal void Configure(DiagnosticLogLevel level)
    { lock (_gate) _level = Enum.IsDefined(level) ? level : DiagnosticLogLevel.Information; }
    internal string? Reference(string kind, string? value) => string.IsNullOrEmpty(value) ? null :
        Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(kind + "\0" + value)))[..12];
    internal static string ModelFamily(string? model)
    {
        foreach (var family in new[] { "deepseek", "openai", "claude", "anthropic", "gemini", "qwen", "llama", "mistral", "jev" })
            if (model?.Contains(family, StringComparison.OrdinalIgnoreCase) == true) return family;
        return "other";
    }
    internal static string? SafeCode(string? code) => code is null ? null : Codes.Contains(code) || Regex.IsMatch(code, "^HTTP_[1-5][0-9]{2}$") ? code : "Other";
    internal static string? SafeMode(string? mode) => mode is null ? null : Modes.Contains(mode) ? mode : "Other";
    private static string ErrorName(Exception error) => error switch
    {
        ObjectDisposedException => "ObjectDisposedException", InvalidDataException => "InvalidDataException", IOException => "IOException",
        OperationCanceledException => "OperationCanceledException", JsonException => "JsonException", System.Net.Http.HttpRequestException => "HttpRequestException",
        UnauthorizedAccessException => "UnauthorizedAccessException", System.ComponentModel.Win32Exception => "Win32Exception",
        CryptographicException => "CryptographicException", ReplyModelException => "ReplyModelException", InvalidOperationException => "InvalidOperationException", _ => "OtherException"
    };
    internal void Write(DiagnosticLogLevel level, DiagnosticEvent name, DiagnosticFields? data = null, Exception? error = null)
    {
        lock (_gate)
        {
            if (level > _level) return;
            try
            {
                data ??= new(); var now = _clock.GetUtcNow();
                for (var depth = 0; depth < 3 && error?.InnerException is not null &&
                    error is AggregateException or System.Reflection.TargetInvocationException; depth++) error = error.InnerException;
                var frames = error is null ? Array.Empty<DiagnosticFrame>() : new StackTrace(error, true).GetFrames()
                    .Where(f => f.GetMethod()?.DeclaringType?.Namespace is { } ns && (ns.StartsWith("QNAgent", StringComparison.Ordinal) || ns.StartsWith("QnocrPoc", StringComparison.Ordinal)))
                    .Take(12).Select(f => new DiagnosticFrame(f.GetMethod()!.DeclaringType!.FullName + "." + f.GetMethod()!.Name,
                        Path.GetFileName(f.GetFileName()), f.GetFileLineNumber())).ToArray();
                var entry = Clean(new DiagnosticEntry
                {
                    Timestamp = now, Level = level, Event = name, RunId = _runId,
                    WindowRef = Reference("window", data.WindowId), SessionRef = Reference("session", data.SessionId), MessageRef = Reference("message", data.MessageId),
                    PreviewRef = Reference("preview", data.PreviewId), ModelRef = Reference("model", data.ModelId), ModelFamily = ModelFamily(data.ModelId),
                    Mode = data.Mode, Code = data.Code ?? (error is ReplyModelException providerError ? providerError.Code : null), Role = data.Role, Success = data.Success, Count = data.Count,
                    Intent = data.Intent, Purpose = data.Purpose, CoverageProbability = data.CoverageProbability, SupportProbability = data.SupportProbability,
                    HttpStatus = data.HttpStatus, DurationMs = data.DurationMs, ContextMessages = data.ContextMessages, SellerMessages = data.SellerMessages,
                    ErrorKind = error is null ? null : ErrorName(error), HResult = error?.HResult, Stack = frames
                });
                Directory.CreateDirectory(DirectoryPath);
                var prefix = "events-" + now.ToString("yyyyMMdd") + "-";
                if (_currentFile is null || !Path.GetFileName(_currentFile).StartsWith(prefix, StringComparison.Ordinal) ||
                    !File.Exists(_currentFile) || new FileInfo(_currentFile).Length >= _maxFileBytes)
                {
                    var today = LogFiles().Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    _currentFile = today.LastOrDefault();
                    if (_currentFile is null || new FileInfo(_currentFile).Length >= _maxFileBytes)
                    {
                        var index = today.Length == 0 ? 0 : int.Parse(Path.GetFileNameWithoutExtension(today[^1]).Split('-')[2]) + 1;
                        _currentFile = Path.Combine(DirectoryPath, prefix + index.ToString("D4") + ".jsonl");
                    }
                    Prune(now);
                }
                File.AppendAllText(_currentFile, JsonSerializer.Serialize(entry, Json) + Environment.NewLine, Encoding.UTF8);
                if (now - _lastPrune > TimeSpan.FromHours(1)) Prune(now);
                // Include the newly created segment in the disk cap.
                var files = LogFiles().OrderByDescending(p => p, StringComparer.Ordinal).ToArray();
                foreach (var old in files.Skip(_maxFiles)) File.Delete(old);
                LastWriteError = null;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { LastWriteError = ErrorName(e); }
        }
    }
    private void Prune(DateTimeOffset now)
    {
        foreach (var file in LogFiles())
        {
            var date = Path.GetFileName(file).Substring(7, 8);
            if (DateTime.TryParseExact(date, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) &&
                day < now.UtcDateTime.Date.AddDays(-7)) File.Delete(file);
        }
        _lastPrune = now;
    }
    internal string[] LogFiles() => !Directory.Exists(DirectoryPath) ? Array.Empty<string>() : Directory.GetFiles(DirectoryPath, "events-*.jsonl")
        .Where(p => Regex.IsMatch(Path.GetFileName(p), "^events-[0-9]{8}-[0-9]{4,8}\\.jsonl$") &&
            (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray();
    internal DiagnosticEntry[] Snapshot(DateTimeOffset since, DateTimeOffset until, out int skipped)
    {
        lock (_gate)
        {
            skipped = 0; var entries = new Queue<DiagnosticEntry>();
            var allFiles = LogFiles().OrderByDescending(p => p, StringComparer.Ordinal).ToArray(); skipped += Math.Max(0, allFiles.Length - _maxFiles);
            foreach (var file in allFiles.Take(_maxFiles).Reverse())
            {
                try
                {
                    if (new FileInfo(file).Length > 4 * 1024 * 1024) { skipped++; continue; }
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.Length > 16384) { skipped++; continue; }
                        try
                        {
                            var entry = JsonSerializer.Deserialize<DiagnosticEntry>(line, Json);
                            if (entry is null || entry.Timestamp < since || entry.Timestamp > until) continue;
                            if (!Enum.IsDefined(entry.Level) || !Enum.IsDefined(entry.Event)) { skipped++; continue; }
                            entries.Enqueue(Clean(entry)); if (entries.Count > 10000) { entries.Dequeue(); skipped++; }
                        }
                        catch (JsonException) { skipped++; }
                    }
                }
                catch (IOException) { skipped++; }
            }
            return entries.ToArray();
        }
    }
    internal static DiagnosticEntry Clean(DiagnosticEntry entry)
    {
        string? Ref(string? s, int size = 12) => s is not null && Regex.IsMatch(s, "^[A-Fa-f0-9]{" + size + "}$") ? s : null;
        var families = new[] { "deepseek", "openai", "claude", "anthropic", "gemini", "qwen", "llama", "mistral", "jev", "other" };
        var errors = new[] { "ObjectDisposedException", "InvalidDataException", "IOException", "OperationCanceledException", "JsonException", "HttpRequestException", "UnauthorizedAccessException", "Win32Exception", "CryptographicException", "ReplyModelException", "InvalidOperationException", "OtherException" };
        double? Probability(double? value) => value is { } p && double.IsFinite(p) && p is >= 0 and <= 1 ? p : null;
        return entry with
        {
            WindowRef = Ref(entry.WindowRef), SessionRef = Ref(entry.SessionRef), MessageRef = Ref(entry.MessageRef), PreviewRef = Ref(entry.PreviewRef), ModelRef = Ref(entry.ModelRef), RunId = Ref(entry.RunId, 32),
            ModelFamily = families.Contains(entry.ModelFamily) ? entry.ModelFamily : "other", Code = SafeCode(entry.Code), Mode = SafeMode(entry.Mode),
            Role = entry.Role is "buyer" or "seller" ? entry.Role : null, ErrorKind = errors.Contains(entry.ErrorKind) ? entry.ErrorKind : null,
            Intent = entry.Intent is not null && JevGateway.Intents.ContainsKey(entry.Intent) ? entry.Intent : null,
            Purpose = entry.Purpose is not null && BuyerIntentDescriptions.Criteria.ContainsKey(entry.Purpose) ? entry.Purpose : null,
            CoverageProbability = Probability(entry.CoverageProbability), SupportProbability = Probability(entry.SupportProbability),
            Stack = (entry.Stack ?? Array.Empty<DiagnosticFrame>()).Where(f => f is not null && f.Method is not null && f.Method.Length <= 200 &&
                Regex.IsMatch(f.Method, @"^(QNAgent|QnocrPoc)\.[A-Za-z0-9_+.<>`|]+$")).Take(12)
                .Select(f => f with { File = f.File is not null && Regex.IsMatch(f.File, @"^[A-Za-z0-9_.-]+\.cs$") ? f.File : null, Line = Math.Max(0, f.Line) }).ToArray()
        };
    }
}
