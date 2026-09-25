using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class StabilizationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = new DateTime(2026, 9, 25, 15, 0, 0);
        BuyerMessage Message(string text, int offset = 0, string product = "") => new() { Text = text, Timestamp = now.AddSeconds(offset), ProductId = product };
        var first = Message("这款多少钱？"); var variant = Message("这款多少钱?");
        check(MessageFingerprint.Create(first) == MessageFingerprint.Create(variant), "fullwidth/ASCII question mark produces stable comparison fingerprint");
        check(MessageFingerprint.Create(Message("TA1  200ml")) == MessageFingerprint.Create(Message("ＴＡ１\r\n200ml")), "width and whitespace OCR variance normalized without changing envelope text");
        check(MessageFingerprint.Create(Message("TA1?")) != MessageFingerprint.Create(Message("TA1!")), "meaningful question/exclamation punctuation remains distinct");
        check(MessageFingerprint.Create(Message("F2U")) != MessageFingerprint.Create(Message("F2V")), "model letters are not fuzzily corrected");
        var tracker = new NewMessageTracker(now);
        tracker.Compare([Message("baseline", -600)], now);
        check(tracker.Compare([Message("first arrival", -1)], now.AddSeconds(2)).NewMessages.Count == 1, "source time one second before PC startup is accepted inside bounded skew window");
        tracker.Compare([first], now);
        check(tracker.Compare([variant], now.AddSeconds(1)).NewMessages.Count == 0, "punctuation variant does not re-enter adapter");
        check(tracker.Compare([Message(first.Text, 1)], now.AddSeconds(1)).NewMessages.Count == 1, "same text with distinct source second remains a real repeat");
        check(tracker.Compare([Message("older unseen history", -300)], now).NewMessages.Count == 0, "old unseen history stays outside bounded acceptance window");
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV14", Guid.NewGuid().ToString("N")));
        var registry = new SessionRegistry(root, "test-run", Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json"));
        var a = registry.Resolve("buyer-A", "1051087533910"); var b = registry.Resolve("buyer-B", ""); var a2 = registry.Resolve("buyer-A", "");
        check(a.SessionId == a2.SessionId && a.SessionId != b.SessionId, "same buyer retains session across product change, distinct buyers isolated");
        check(a.BuyerId != b.BuyerId && a.IdentityPath != b.IdentityPath, "test identity mapping cannot merge distinct observed buyers in qnbot");
        check(a2.ProductId == "", "session records current empty product instead of carrying old ID");
        check(MessageFingerprint.StableId(a.SessionId, first) != MessageFingerprint.StableId(b.SessionId, first), "stable MessageId includes buyer session");
        check(MessageFingerprint.StableId(a.SessionId, first) == MessageFingerprint.StableId(a2.SessionId, Message(first.Text, product: "1051087533910")), "visible product update does not create another message ID");
        DispatchItem Item(BuyerMessage m, MessageSession session) => new(MessageFingerprint.StableId(session.SessionId, m), MessageFingerprint.Create(m), session,
            new("Qianniu.OCR", session.SellerId, session.BuyerId, m.ProductId, [new(m.Role, m.Text, m.Timestamp)]), DateTimeOffset.Now, false);
        var item = Item(first, a); var calls = 0;
        Task<ShadowAdapterResult> Convert(string e, string? i, string o, CancellationToken t) { calls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null)); }
        using (var box = new DurableShadowDispatch(root, Path.Combine(root, "life.jsonl"), CancellationToken.None, Convert))
        {
            check(box.Submit(item) && !box.Submit(Item(variant, a)), "durable outbox rejects duplicate before adapter");
            box.Start(); await box.StopAsync();
            check(calls == 1 && box.Snapshot().Single().State == "Completed", "one durable item dispatches exactly once");
        }
        using (var restarted = new DurableShadowDispatch(root, Path.Combine(root, "life.jsonl"), CancellationToken.None, Convert))
        {
            check(!restarted.Submit(item), "acknowledged message is not replayed after host restart");
            restarted.Start(); await restarted.StopAsync(); check(calls == 1, "restart retains completed idempotency ledger");
        }
        var retryRoot = root + "-retry"; var attempt = 0;
        using (var retry = new DurableShadowDispatch(retryRoot, Path.Combine(retryRoot, "life.jsonl"), CancellationToken.None,
            (_, _, _, _) => Task.FromResult(++attempt < 3 ? new ShadowAdapterResult("HostUnavailable", 0, null, "test") : new("Converted", 1, null, null))))
        {
            retry.Submit(item); retry.Start(); await retry.StopAsync();
            check(attempt == 3 && retry.Snapshot().Single().State == "Completed", "explicit not-started failures back off and recover without dropping item");
        }
        var failedRoot = root + "-uncertain";
        using (var uncertain = new DurableShadowDispatch(failedRoot, Path.Combine(failedRoot, "life.jsonl"), CancellationToken.None,
            (_, _, _, _) => throw new IOException("injected unknown transport outcome")))
        {
            uncertain.Submit(item); uncertain.Start(); await uncertain.StopAsync();
            check(uncertain.Snapshot().Single().State == "Uncertain", "adapter exception leaves durable uncertainty and does not exit worker");
        }
        using (var resumed = new DurableShadowDispatch(failedRoot, Path.Combine(failedRoot, "life.jsonl"), CancellationToken.None, Convert))
        {
            resumed.Start(); await resumed.StopAsync();
            check(calls == 1 && resumed.Snapshot().Single().State == "Uncertain", "unknown prior outcome is never blindly replayed on restart");
        }
        var crashRoot = root + "-crash";
        Directory.CreateDirectory(Path.Combine(crashRoot, "outbox", item.MessageId));
        File.WriteAllText(Path.Combine(crashRoot, "outbox", item.MessageId, "item.json"), JsonSerializer.Serialize(item with { State = "Dispatching" }));
        using (var crash = new DurableShadowDispatch(crashRoot, Path.Combine(crashRoot, "life.jsonl"), CancellationToken.None, Convert))
            check(crash.Snapshot().Single().State == "Uncertain", "crashed in-flight dispatch is held for reconciliation");
        var corruptRoot = root + "-corrupt";
        Directory.CreateDirectory(Path.Combine(corruptRoot, "outbox", item.MessageId));
        File.WriteAllText(Path.Combine(corruptRoot, "outbox", item.MessageId, "item.json"), "broken json");
        using (var corrupt = new DurableShadowDispatch(corruptRoot, Path.Combine(corruptRoot, "life.jsonl"), CancellationToken.None, Convert))
            check(corrupt.CorruptStateFiles == 1 && !corrupt.Submit(item), "corrupt ledger is held, never overwritten to produce duplicate delivery");
        var life = File.ReadLines(Path.Combine(root, "life.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        check(life.All(e => e.TryGetProperty("CaptureTime", out _) && e.TryGetProperty("Source", out _) && e.TryGetProperty("Buyer", out _)
            && e.TryGetProperty("ProductId", out _) && e.TryGetProperty("MessageId", out _) && e.TryGetProperty("AdapterResult", out _)
            && e.TryGetProperty("DispatchResult", out _) && e.GetProperty("AiState").GetString() == "OFF"), "every lifecycle record contains required diagnosis fields and AI OFF");
    }
}
