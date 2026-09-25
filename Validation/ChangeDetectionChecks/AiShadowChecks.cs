using System.Text.Json;
using QnocrPoc.Shadow;

internal static class AiShadowChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV13", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        var dispatcher = new AiShadowDispatcher(root, CancellationToken.None, async (e, i, o, t) =>
        {
            seen.Add(e); entered.TrySetResult();
            await release.Task;
            return new("Converted", 1, null, null, true, "Synthetic preview");
        });
        var queued = await dispatcher.EnqueueAsync("first.json", "test.json", root, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(queued.Status == "AiQueued" && !queued.AiInvoked && queued.ReplySuggestion is null, "queue acknowledgement is not misreported as AI output");
        check((await dispatcher.EnqueueAsync("second.json", "test.json", root, CancellationToken.None)).Status == "AiQueued", "capture can queue second event while first AI call is pending");
        release.SetResult();
        var results = await dispatcher.CompleteAsync();
        check(results.Count == 2 && seen.SequenceEqual(new[] { "first.json", "second.json" }), "preview queue drains in order without lost events");
        check(File.ReadAllLines(Path.Combine(root, "ai-shadow.jsonl")).All(line => !JsonDocument.Parse(line).RootElement.GetProperty("SendEnabled").GetBoolean()), "preview queue log always records Send OFF");
        check((await dispatcher.EnqueueAsync("third.json", "test.json", root, CancellationToken.None)).Status == "AiQueueFull", "closed queue does not claim acceptance");
        using var cancellation = new CancellationTokenSource();
        var called = false;
        cancellation.Cancel();
        var cancelled = new AiShadowDispatcher(root, cancellation.Token, (_, _, _, _) => { called = true; throw new Exception("must not run"); });
        await cancelled.EnqueueAsync("cancelled.json", "test.json", root, CancellationToken.None);
        var cancelledResults = await cancelled.CompleteAsync();
        check(!called && cancelledResults.Single().Status == "AiCancelled", "cancelled observation never invokes queued AI");
        var failed = new AiShadowDispatcher(root, CancellationToken.None, (_, _, _, _) => throw new IOException("simulated endpoint failure"));
        await failed.EnqueueAsync("failure.json", "test.json", root, CancellationToken.None);
        check((await failed.CompleteAsync()).Single().Status == "AiFailed", "AI failures are explicit results without reply fabrication");
        try { await new QnocrShadowRunner().RunAsync(new(1, 0, null, true)); check(false, "AI mapping required"); }
        catch (InvalidDataException) { check(true, "AI mode requires explicit test identity before capture"); }
    }
}
