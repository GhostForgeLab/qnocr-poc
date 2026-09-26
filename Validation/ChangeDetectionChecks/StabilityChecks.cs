using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class StabilityChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV110", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var t0 = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(8));
        var sid = new string('A', 64);
        var product = new ProductReference("1051087533910", "SyntheticFixture", t0, "capture-one", "Unverified");
        var session = new MessageSession(sid, "seller-test", "buyer-test:A", "buyer-A", "IsolatedTestIdentity",
            product.ProductId, "test-identity.json", "buyer-A", t0, t0, [product]);
        var stateRoot = Path.Combine(root, "state");
        var lifecycle = new SessionRunLifecycle(stateRoot, Path.Combine(root, "run-one"));
        lifecycle.Observe(session, t0);
        check(lifecycle.Snapshot().Active == 1 && lifecycle.Snapshot().ProductReferences == 1,
            "session lifecycle exposes active count and product-reference count");
        lifecycle.SweepInactive(t0.AddMinutes(31), TimeSpan.FromMinutes(30));
        check(lifecycle.Snapshot().Inactive == 1 && lifecycle.Snapshot().Active == 0,
            "idle session becomes inactive without losing its reference");
        lifecycle.Observe(session, t0.AddMinutes(32));
        check(lifecycle.Snapshot().Active == 1 && lifecycle.Snapshot().Reopened == 1,
            "new activity resumes the same session after inactivity");
        lifecycle.CloseAll(t0.AddMinutes(40));
        check(lifecycle.Snapshot().Closed == 1 && lifecycle.Snapshot().Active == 0,
            "run shutdown closes observed session lifecycle without deleting buyer state");
        var resumed = new SessionRunLifecycle(stateRoot, Path.Combine(root, "run-two"));
        resumed.Observe(session, t0.AddMinutes(41));
        var runState = File.ReadAllText(Path.Combine(stateRoot, "sessions", sid, "run-state.json"));
        check(resumed.Snapshot().Active == 1 && resumed.Snapshot().Reopened == 1
            && runState.Contains("1051087533910") == false,
            "restart reopens the same session; product IDs remain in session.json rather than lifecycle metadata");

        var logRoot = Path.Combine(root, "rolling-logs");
        var large = new string('x', (int)(RunLogWriter.MaxActiveBytes / 2));
        RunLogWriter.Append(logRoot, "audit.jsonl", new { Sequence = 1, Body = large });
        RunLogWriter.Append(logRoot, "audit.jsonl", new { Sequence = 2, Body = large });
        var archives = Directory.GetFiles(Path.Combine(logRoot, "log-archive"), "*.jsonl");
        check(archives.Length == 1 && File.ReadAllText(archives[0]).Contains("\"Sequence\":1")
            && File.ReadAllText(Path.Combine(logRoot, "audit.jsonl")).Contains("\"Sequence\":2"),
            "large audit log rolls into a preserved archive segment without deleting either record");

        DispatchItem Item(char id, string text, bool ai) => new(new string(id, 64), new string(id, 64), session,
            new MessageEnvelope("Qianniu.OCR", session.SellerId, session.BuyerId, product.ProductId,
                [new EnvelopeMessage("buyer", text, t0.DateTime)]) { ProductBinding = "SyntheticUnverified" },
            t0.AddSeconds(id - 'A'), ai, CaptureId: "capture-" + id,
            AiRequestId: ai ? MessageFingerprint.AiRequestId(new string(id, 64)) : null);
        var fail = Item('B', "first AI failure", true);
        var success = Item('C', "later AI success", true);
        var trace = new AgentPipelineTrace(Path.Combine(root, "ai-run"));
        var aiCalls = 0;
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "ai-state"),
            Path.Combine(root, "ai-run", "life.jsonl"), CancellationToken.None,
            (envelope, _, _, _) =>
            {
                aiCalls++;
                return Task.FromResult(envelope.Contains(fail.MessageId, StringComparison.Ordinal)
                    ? new ShadowAdapterResult("AiFailed", 0, null, "Injected AI failure", true)
                    : new ShadowAdapterResult("Converted", 1, null, null, true, "Synthetic later preview", AiCalls: 1));
            }, true, trace))
        {
            dispatch.Submit(fail, SessionContextBuilder.Build(fail, []));
            dispatch.Submit(success, SessionContextBuilder.Build(success, [fail]));
            dispatch.Start(); await dispatch.StopAsync();
            var snapshots = dispatch.Snapshot();
            check(aiCalls == 2 && snapshots.Single(i => i.MessageId == fail.MessageId).State == "AiFailed"
                && snapshots.Single(i => i.MessageId == success.MessageId).State == "Completed"
                && File.Exists(Path.Combine(root, "ai-state", "outbox", success.MessageId, "reply-preview.json")),
                "AI failure is held while the next message in the same session still reaches a preview");
        }
        var transportCalls = 0;
        var thrown = Item('D', "transport exception", false);
        var later = Item('E', "later dispatch", false);
        using (var dispatch = new DurableShadowDispatch(Path.Combine(root, "dispatch-state"),
            Path.Combine(root, "dispatch-run", "life.jsonl"), CancellationToken.None,
            (envelope, _, _, _) =>
            {
                transportCalls++;
                if (envelope.Contains(thrown.MessageId, StringComparison.Ordinal)) throw new IOException("Injected single-item fault");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null));
            }, false))
        {
            dispatch.Submit(thrown); dispatch.Submit(later);
            dispatch.Start(); await dispatch.StopAsync();
            var snapshots = dispatch.Snapshot();
            check(transportCalls == 2 && snapshots.Single(i => i.MessageId == thrown.MessageId).State == "Uncertain"
                && snapshots.Single(i => i.MessageId == later.MessageId).State == "Completed",
                "single-message adapter exception cannot stop later durable dispatch");
        }
        using (var restarted = new DurableShadowDispatch(Path.Combine(root, "dispatch-state"),
            Path.Combine(root, "dispatch-run-two", "life.jsonl"), CancellationToken.None,
            (_, _, _, _) => { transportCalls++; return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null)); }, false))
        {
            check(!restarted.Submit(later) && restarted.Snapshot().Single(i => i.MessageId == later.MessageId).State == "Completed"
                && restarted.Snapshot().Single(i => i.MessageId == thrown.MessageId).State == "Uncertain",
                "restart restores completed dedupe and holds uncertain outcome without replay");
            restarted.Start(); await restarted.StopAsync();
            check(transportCalls == 2, "restart never re-dispatches an already completed or uncertain message");
        }
    }
}
