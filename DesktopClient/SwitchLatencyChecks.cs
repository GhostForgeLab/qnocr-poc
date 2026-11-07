using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Capture;
using QnocrPoc.Models;
using QnocrPoc.Shadow;
using QnocrPoc.Messages;

namespace QNAgent;

internal static class SwitchLatencyChecks
{
    public static async Task<int> Run(bool live)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        var cache = new ChatRecognitionCache();
        var a = new OcrResult("memory:a", "test", Array.Empty<OcrLine>(), 0, null, 0, 0, 0, 0, 0, 0);
        cache.Add("chat-and-header-a", a);
        cache.Add("chat-and-header-b", a with { ImagePath = "memory:b" });
        Check(cache.TryGet("chat-and-header-a", out var again) && ReferenceEquals(again, a), "return to identical A frame reuses OCR after viewing B");
        Check(!cache.TryGet("same-chat-different-header", out _), "changed buyer header cannot reuse recognition");
        Check(!cache.TryGet("new-message-a", out _), "new message cannot reuse old recognition");
        for (var i = 0; i < 24; i++) cache.Add("fresh-" + i, a);
        Check(!cache.TryGet("chat-and-header-a", out _), "recognition cache is bounded to 24 frames");
        cache.TryGet("fresh-0", out _); cache.Add("fresh-24", a);
        Check(cache.TryGet("fresh-0", out _) && !cache.TryGet("fresh-1", out _), "recently reused frames survive LRU eviction");
        using (var connection = new QianniuConnection(new ClientStore(Path.Combine("Output", "SwitchChecks", Guid.NewGuid().ToString("N")))))
        {
            ObservedConversation? observed = null; connection.Conversation += state => observed = state;
            var waiting = connection.WaitForWakeAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            var timer = Stopwatch.StartNew(); connection.NotifyConversationClick();
            Check(await waiting && timer.ElapsedMilliseconds < 500, "buyer click interrupts the two-second idle wait");
            Check(observed is { Buyer: null }, "click withdraws old buyer before OCR starts");
            Check(!connection.IsCurrent(new("shop", "previous-buyer", "late result", 0)), "queued old-buyer publication is rejected after click");
            for (var i = 0; i < 100; i++) connection.NotifyConversationClick();
            Check(await connection.WaitForWakeAsync(TimeSpan.Zero, CancellationToken.None) &&
                !await connection.WaitForWakeAsync(TimeSpan.Zero, CancellationToken.None), "rapid clicks coalesce without a work queue");
        }
        if (live)
        {
            var window = QianniuConnection.Windows().FirstOrDefault(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked)
                ?? throw new InvalidOperationException("请显示千牛接待窗口后运行实测。");
            await using var worker = await MemoryNativeWorker.StartAsync(CancellationToken.None);
            var capture = await worker.CaptureAsync(window, CancellationToken.None);
            var frame = MemoryChatFrame.FromPng(capture.Png ?? throw new Exception("No live frame"));
            await worker.RecognizeAsync(frame.Chat, "memory:warmup", CancellationToken.None);
            var watch = Stopwatch.StartNew();
            var chat = await worker.RecognizeAsync(frame.Chat, "memory:timing-chat", CancellationToken.None);
            var chatMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var focused = await worker.RecognizeChatAsync(frame.Chat, "memory:timing-focused", CancellationToken.None);
            var focusedMs = watch.ElapsedMilliseconds;
            var fullMessages = new BuyerMessageStructurer(true).Structure(chat, frame.Chat).Messages;
            var focusedMessages = new BuyerMessageStructurer(true).Structure(focused, frame.Chat).Messages;
            Console.WriteLine($"Full/focused timing={chatMs}/{focusedMs}ms, OCR lines={chat.Lines.Count}/{focused.Lines.Count}, accepted={fullMessages.Count}/{focusedMessages.Count}");
            Console.WriteLine("Full accepted shape=" + string.Join(",", fullMessages.Select(m => $"{m.ContentKind}:{m.Text.Length}")) +
                "; focused=" + string.Join(",", focusedMessages.Select(m => $"{m.ContentKind}:{m.Text.Length}")));
            Check(fullMessages.All(m => focusedMessages.Any(f => f.Text == m.Text && f.Timestamp == m.Timestamp)),
                "focused OCR retains all previously accepted live message text and timestamps");
            Console.WriteLine($"Focused buyer-region OCR={focusedMs}ms vs full chat={chatMs}ms, accepted messages={focusedMessages.Count}.");
            if (Environment.ProcessorCount >= 8)
            {
                await using var balanced = await MemoryNativeWorker.StartAsync(CancellationToken.None, cpuThreads: 2);
                await balanced.RecognizeChatAsync(frame.Chat, "memory:balanced-warmup", CancellationToken.None);
                watch.Restart();
                var faster = await balanced.RecognizeChatAsync(frame.Chat, "memory:balanced", CancellationToken.None);
                var fasterMs = watch.ElapsedMilliseconds;
                var fasterMessages = new BuyerMessageStructurer(true).Structure(faster, frame.Chat).Messages;
                Check(focusedMessages.Select(m => (m.Text, m.Timestamp)).SequenceEqual(fasterMessages.Select(m => (m.Text, m.Timestamp))),
                    "two-thread balanced mode preserves focused recognition");
                Console.WriteLine($"Balanced two-thread focused OCR={fasterMs}ms vs single-thread focused={focusedMs}ms.");
            }
            watch.Restart();
            await worker.RecognizeAsync(frame.Product, "memory:timing-product", CancellationToken.None);
            var productMs = watch.ElapsedMilliseconds;
            cache.Add(frame.ChatFingerprint, chat); watch.Restart();
            Check(cache.TryGet(frame.ChatFingerprint, out _), "live exact frame can reuse recognition");
            Console.WriteLine($"Same frame benchmark: chat OCR={chatMs}ms, product OCR={productMs}ms, former sequential OCR={chatMs + productMs}ms; cached lookup={watch.Elapsed.TotalMilliseconds:F2}ms.");
            Console.WriteLine("These are stage timings, not end-to-end switch latency; no chat text or screenshots saved.");
        }
        Console.WriteLine($"Passed {count} switch latency checks."); return 0;
    }
}
