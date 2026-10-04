using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Adapter;
using QnocrPoc.Capture;
using QnocrPoc.Layout;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Bounded, read-only capture of explicitly marked test messages. No business dispatch.</summary>
public static class AgentContextCaptureCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        const string usage = "--agent-context-capture --database <file> --output <new-directory> --hwnd <hex> --prefix <unique-test-marker> --buyer-label <expected-observed-label> --frames <1..600> --interval-ms <1000..60000> [--intent-config <file>] [--product-state <directory> --knowledge-config <file> --knowledge-workflow <file>] [--reply-state <directory> --ai-provider-config <file>]";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length is not (15 or 17 or 23 or 27)) { Console.Error.WriteLine(usage); return 2; }
        for (var i = 1; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { Console.Error.WriteLine(usage); return 2; }
        if (!values.TryGetValue("--database", out var database) ||
            !values.TryGetValue("--output", out var output) ||
            !values.TryGetValue("--prefix", out var prefix) || prefix.Length < 6 ||
            !values.TryGetValue("--buyer-label", out var expectedBuyerLabel) ||
            !values.TryGetValue("--hwnd", out var handle) ||
            !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hwnd) || hwnd <= 0 ||
            !values.TryGetValue("--frames", out var frameText) || !int.TryParse(frameText, out var frames) || frames is < 1 or > 600 ||
            !values.TryGetValue("--interval-ms", out var intervalText) || !int.TryParse(intervalText, out var interval) || interval is < 1000 or > 60000)
        { Console.Error.WriteLine(usage); return 2; }
        var intentEnabled = values.TryGetValue("--intent-config", out var intentConfig);
        var hasProductState = values.TryGetValue("--product-state", out var productState);
        var hasKnowledgeConfig = values.TryGetValue("--knowledge-config", out var knowledgeConfig);
        var hasWorkflow = values.TryGetValue("--knowledge-workflow", out var workflow);
        var knowledgeEnabled = hasProductState && hasKnowledgeConfig && hasWorkflow;
        var hasReplyState = values.TryGetValue("--reply-state", out var replyState);
        var hasAiConfig = values.TryGetValue("--ai-provider-config", out var aiConfig);
        var replyEnabled = hasReplyState && hasAiConfig;
        if (args.Length >= 17 && !intentEnabled || args.Length >= 23 && !knowledgeEnabled ||
            args.Length < 23 && (hasProductState || hasKnowledgeConfig || hasWorkflow) ||
            args.Length == 27 && !replyEnabled || args.Length != 27 && (hasReplyState || hasAiConfig))
        { Console.Error.WriteLine(usage); return 2; }
        var normalizer = new IdentityNormalizer();
        var normalizedExpectedBuyerLabel = normalizer.Normalize(expectedBuyerLabel);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("Use a new output directory to keep capture evidence separate.");
        var capture = new QianniuWindowCapture();
        var window = capture.FindWindows(_ => { }).SingleOrDefault(w => w.Hwnd == (nint)hwnd &&
            w.IsVisible && !w.IsMinimized && !w.IsCloaked && w.WindowTitle.Contains("接待中心", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected Qianniu reception window must be visible.");
        // Stable observation labels, never native platform IDs. HWND and capture time are excluded.
        var sellerId = ObservedId("seller", window.WindowTitle);
        Directory.CreateDirectory(output);
        using var pipeline = intentEnabled ? null : new AgentContextPipeline(database);
        using var intentPipeline = intentEnabled && !knowledgeEnabled ? new AgentIntentPipeline(database, intentConfig!) : null;
        using var knowledgePipeline = knowledgeEnabled && !replyEnabled ? new AgentKnowledgePipeline(database,
            productState!, knowledgeConfig!, workflow!, intentConfig!) : null;
        using var replyPipeline = replyEnabled ? new AgentReplyPipeline(database, productState!, knowledgeConfig!,
            workflow!, replyState!, aiConfig!, intentConfig!) : null;
        var databasePath = replyPipeline?.DatabasePath ?? knowledgePipeline?.DatabasePath ?? intentPipeline?.DatabasePath ?? pipeline!.DatabasePath;
        var mode = replyEnabled ? "ShadowReplyPreview" : knowledgeEnabled ? "ShadowContextIntentKnowledge" : intentEnabled ? "ShadowContextIntent" : "ShadowContextOnly";
        var intentResults = new List<AgentIntentMessageResult>();
        var knowledgeResults = new List<AgentKnowledgeMessageResult>();
        var replyResults = new List<AgentReplyMessageResult>();
        async Task<AgentSession> Process(MessageEnvelope envelope, string receiptPath,
            OcrResult? productOcr, string captureId, DateTimeOffset capturedAt)
        {
            if (replyPipeline is not null)
            {
                var reply = await replyPipeline.ProcessMessageAsync(envelope, productOcr, captureId, capturedAt, cancellationToken);
                intentResults.AddRange(reply.Knowledge.IntentResults);
                knowledgeResults.AddRange(reply.Knowledge.Results);
                replyResults.AddRange(reply.Results);
                Write(receiptPath, reply);
                return reply.Knowledge.Session;
            }
            if (knowledgePipeline is not null)
            {
                var knowledge = await knowledgePipeline.ProcessMessageAsync(envelope, productOcr, captureId,
                    capturedAt, cancellationToken);
                intentResults.AddRange(knowledge.IntentResults);
                knowledgeResults.AddRange(knowledge.Results);
                Write(receiptPath, knowledge);
                return knowledge.Session;
            }
            if (intentPipeline is null) return pipeline!.ProcessMessage(envelope);
            var result = intentPipeline.ProcessMessage(envelope);
            intentResults.AddRange(result.Results);
            Write(receiptPath, result);
            return result.Session;
        }
        var startedAt = DateTimeOffset.UtcNow;
        Write(Path.Combine(output, "session.json"), new
        {
            Version = replyEnabled ? "1.35.0-C" : knowledgeEnabled ? "1.35.0-B" : intentEnabled ? "1.35.0-A" : "1.34.0-B", Mode = mode, StartedAt = startedAt,
            DatabasePath = databasePath, TestPrefix = prefix, ExpectedBuyerLabel = expectedBuyerLabel, SellerId = sellerId,
            NormalizedExpectedBuyerLabel = normalizedExpectedBuyerLabel,
            WindowHandle = handle, WindowTitle = window.WindowTitle,
            IdentityLevel = "Observed", frames, interval,
            AiCalls = 0, IntentCalls = 0, IntentEnabled = intentEnabled, KnowledgeEnabled = knowledgeEnabled, ReplyEnabled = replyEnabled, KnowledgeCalls = 0, SendEnabled = false, SentMessages = 0
        });
        var native = new NativeWorkerClient();
        var structurer = new BuyerMessageStructurer();
        var envelopes = 0;
        var duplicateChecks = 0;
        var failedFrames = 0;
        var completedFrames = 0;
        var ocrCalls = 0;
        string? previousHash = null;
        Console.WriteLine($"CONTEXT_CAPTURE_READY: {output}; Prefix={prefix}; AI={(replyEnabled ? "PreviewOnly" : "OFF")}; Intent={replyPipeline?.SelectedIntentProvider ?? knowledgePipeline?.SelectedIntentProvider ?? intentPipeline?.SelectedProvider ?? "OFF"}; Knowledge={(knowledgeEnabled ? "PublishedOnly" : "OFF")}; Send=OFF");
        for (var frame = 1; frame <= frames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(output, "stop.request"))) break;
            var current = capture.FindWindows(_ => { }).SingleOrDefault(w => w.Hwnd == window.Hwnd &&
                w.Pid == window.Pid && w.WindowTitle == window.WindowTitle && w.IsVisible && !w.IsMinimized && !w.IsCloaked);
            if (current is null) throw new InvalidOperationException("Selected reception window changed or became unavailable.");
            var slot = Path.Combine(output, "frames", frame.ToString("D4", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(slot);
            var result = await native.CaptureAsync(current, slot, cancellationToken);
            Write(Path.Combine(slot, "capture.json"), result);
            completedFrames++;
            if (!result.Success || result.SavedPath is null)
            {
                failedFrames++;
                Console.WriteLine($"CAPTURE_FAILED: frame={frame}; {result.Message}");
            }
            else
            {
                var layout = await new QianniuLayoutResolver().ResolveAsync(result.SavedPath);
                Write(Path.Combine(slot, "layout.json"), layout);
                new QianniuRegionExtractor().Extract(result.SavedPath, slot, layout.ToPixelLayout());
                var chat = Path.Combine(slot, "chat_region.png");
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(chat, cancellationToken)));
                if (hash != previousHash)
                {
                    var ocr = await native.RecognizeAsync(chat, cancellationToken);
                    ocrCalls++;
                    var batch = structurer.Structure(ocr);
                    Write(Path.Combine(slot, "buyer-messages.json"), batch);
                    var indices = Enumerable.Range(0, batch.Messages.Count)
                        .Where(index => batch.Messages[index].Text.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    var selected = batch with
                    {
                        Messages = indices.Select(index => batch.Messages[index]).ToArray(),
                        Evidence = indices.Select((index, ordinal) =>
                            batch.Evidence.Single(e => e.MessageIndex == index) with { MessageIndex = ordinal }).ToArray()
                    };
                    var buyerLabel = SessionRegistry.ReadBuyer(selected, ocr);
                    string? normalizedBuyerLabel = null;
                    if (buyerLabel is not null)
                    {
                        try { normalizedBuyerLabel = normalizer.Normalize(buyerLabel); }
                        catch (ArgumentException) { /* Invalid/empty OCR candidates remain Held. */ }
                    }
                    if (indices.Length > 0 && normalizedBuyerLabel == normalizedExpectedBuyerLabel)
                    {
                        // Normalize before the existing observation hash; a hash cannot repair label aliases.
                        var buyerId = ObservedId("buyer", normalizedBuyerLabel);
                        Write(Path.Combine(slot, "identity.json"), new { SellerId = sellerId,
                            BuyerId = buyerId, BuyerLabel = buyerLabel, NormalizedBuyerLabel = normalizedBuyerLabel,
                            IdentityLevel = "Observed" });
                        OcrResult? productOcr = null;
                        var capturedAt = DateTimeOffset.UtcNow;
                        var captureId = Path.GetFullPath(slot);
                        if (knowledgeEnabled)
                        {
                            try
                            {
                                productOcr = await native.RecognizeAsync(Path.Combine(slot, "product_region.png"), cancellationToken);
                                Write(Path.Combine(slot, "product-ocr.json"), productOcr);
                            }
                            catch (Exception error) when (error is not OutOfMemoryException &&
                                !(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
                            {
                                Write(Path.Combine(slot, "product-ocr-error.json"), new { Error = error.GetType().Name });
                            }
                        }
                        IMessageSource source = new OcrMessageSource(_ => Task.FromResult(selected),
                            sellerId, buyerId);
                        await foreach (var envelope in source.ReadAsync(cancellationToken))
                        {
                            var number = ++envelopes;
                            var envelopePath = Path.Combine(slot, $"envelope_{number:D4}.json");
                            Write(envelopePath, envelope);
                            var receiptPrefix = replyEnabled ? "reply" : knowledgeEnabled ? "knowledge" : "intent";
                            var context = await Process(envelope, Path.Combine(slot, $"{receiptPrefix}_{number:D4}.json"), productOcr, captureId, capturedAt);
                            // A fresh DTO traverses Stable Identity and Manager again, without upstream dedupe.
                            var retry = JsonSerializer.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath))!;
                            var beforeIntentCount = intentResults.Count;
                            var beforeKnowledgeCount = replyPipeline?.KnowledgeCalls ?? knowledgePipeline?.KnowledgeCalls ?? 0;
                            var beforeAiCount = replyPipeline?.AiCalls ?? 0;
                            var beforePreviewCount = replyPipeline?.PreviewCount ?? 0;
                            var repeated = await Process(retry, Path.Combine(slot, $"{receiptPrefix}_replay_{number:D4}.json"), productOcr, captureId, capturedAt);
                            if ((replyPipeline?.KnowledgeCalls ?? knowledgePipeline?.KnowledgeCalls ?? 0) != beforeKnowledgeCount)
                                throw new InvalidDataException("Repeated envelope retriggered Knowledge.");
                            if ((replyPipeline?.AiCalls ?? 0) != beforeAiCount || (replyPipeline?.PreviewCount ?? 0) != beforePreviewCount)
                                throw new InvalidDataException("Repeated envelope retriggered AI/Preview.");
                            if (intentResults.Count != beforeIntentCount) throw new InvalidDataException("Repeated envelope retriggered Intent.");
                            if (!ReferenceEquals(context, repeated)) throw new InvalidDataException("Repeated envelope changed Context.");
                            duplicateChecks++;
                            Write(Path.Combine(slot, $"context_{number:D4}.json"), context);
                            Console.WriteLine($"CONTEXT_SAVED: frame={frame}; session={context.SessionId}; messages={context.Messages.Count}; duplicate=ignored");
                        }
                    }
                    else if (indices.Length > 0) Console.WriteLine($"IDENTITY_HELD: frame={frame}; OCR buyer header does not match the explicit test label");
                    previousHash = hash;
                }
            }
            if (frame < frames) await Task.Delay(interval, cancellationToken);
        }
        var sessions = new SQLiteAgentSessionStore(databasePath).LoadSessions();
        Write(Path.Combine(output, "summary.json"), new
        {
            Mode = mode, StartedAt = startedAt, EndedAt = DateTimeOffset.UtcNow,
            CompletedFrames = completedFrames, FailedFrames = failedFrames, OcrCalls = ocrCalls,
            Envelopes = envelopes, DuplicateChecks = duplicateChecks, Sessions = sessions.Count,
            StoredMessages = sessions.Sum(s => s.Messages.Count),
            AiCalls = replyPipeline?.AiCalls ?? 0, IntentCalls = intentResults.Count,
            KnowledgeCalls = replyPipeline?.KnowledgeCalls ?? knowledgePipeline?.KnowledgeCalls ?? 0,
            ReplyPreviewCount = replyPipeline?.PreviewCount ?? 0,
            JEVSuccess = intentResults.Count(item => item.Provider == "JEV" && item.Error is null),
            RuleFallbackCount = intentResults.Count(item => item.FallbackUsed),
            IntentErrors = intentResults.Count(item => item.Error is not null),
            AverageIntentLatencyMilliseconds = intentResults.Count == 0 ? 0 : intentResults.Average(item => item.LatencyMilliseconds),
            KnowledgeAvailable = knowledgeResults.Count(item => item.QueryStatus == KnowledgeStatuses.Available),
            KnowledgeErrors = knowledgeResults.Count(item => item.QueryStatus == AgentKnowledgePipeline.KnowledgeUnavailable),
            SendEnabled = false, SentMessages = 0,
            Status = replyResults.Any(item => item.Error is not null) ? "CapturedWithReplyErrors"
                : knowledgeResults.Any(item => item.Error is not null) ? "CapturedWithKnowledgeErrors"
                : intentResults.Any(item => item.Error is not null) ? "CapturedWithIntentErrors"
                : envelopes > 0 ? "Captured" : "NoControlledMessages"
        });
        Console.WriteLine($"CONTEXT_CAPTURE_END: Envelopes={envelopes}; DuplicateChecks={duplicateChecks}; Sessions={sessions.Count}; IntentCalls={intentResults.Count}; KnowledgeCalls={replyPipeline?.KnowledgeCalls ?? knowledgePipeline?.KnowledgeCalls ?? 0}; AiCalls={replyPipeline?.AiCalls ?? 0}; Previews={replyPipeline?.PreviewCount ?? 0}; SentMessages=0");
        return envelopes > 0 && intentResults.All(item => item.Error is null) &&
            knowledgeResults.All(item => item.Error is null) && replyResults.All(item => item.Error is null) ? 0 : 3;
    }

    private static string ObservedId(string role, string label) =>
        "observed:" + role + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label))).ToLowerInvariant();

    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
