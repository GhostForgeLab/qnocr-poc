using System.Diagnostics;
using QnocrPoc.Extension.AgentBridge;
using QnocrPoc.RuntimeObserver;

internal static class RuntimeObserverChecks
{
    public static async Task<int> RunChildAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await RuntimeObserverRunner.RunAsync(args[1], args[2], 1, TimeSpan.Zero, deadline.Token);
        return 0;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        IReadOnlyCollection<int> ids = [];
        IRuntimeObserver observer = new ExternalProcessObserver("qn_synthetic", _ => ids);
        var initial = observer.Poll();
        check(initial.Select(x => x.EventType).SequenceEqual(["ObserverStarted", "TargetAbsent"]),
            "external observer starts and reports an absent target");
        ids = [43210];
        var started = observer.Poll();
        check(started.Count == 1 && started[0].EventType == "TargetStarted" &&
            started[0].Metadata["ProcessId"] == "43210", "external observer detects a target start");
        check(observer.Poll().Count == 0, "unchanged process state does not produce duplicate events");
        ids = [];
        var exited = observer.Poll();
        check(exited.Count == 1 && exited[0].EventType == "TargetExited" &&
            observer.Stop().EventType == "ObserverStopped", "external observer detects exit and stops");
        check(initial.Concat(started).Concat(exited).All(x => x.IdentityStatus == "Observed" &&
            x.Source == RuntimeObservedEvent.ObserverSource &&
            x.ToAgentEventEnvelope().IdentityStatus == "Observed"),
            "runtime events remain Observed across the bridge mapping");

        var invalid = started[0].ToAgentEventEnvelope() with { IdentityStatus = "Verified" };
        try { invalid.ValidateForTransport(); check(false, "bridge rejects Verified runtime identity"); }
        catch (InvalidDataException) { check(true, "bridge rejects Verified runtime identity"); }
        invalid = started[0].ToAgentEventEnvelope() with { BuyerId = "buyer-test" };
        try { invalid.ValidateForTransport(); check(false, "runtime event cannot carry buyer fields"); }
        catch (InvalidDataException) { check(true, "runtime event cannot carry buyer fields"); }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing validation executable path.");
        var targetName = Process.GetCurrentProcess().ProcessName;
        var parentPid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pipeName = "qn_runtime_v1323a_" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--runtime-observer-child");
        start.ArgumentList.Add(targetName);
        start.ArgumentList.Add(pipeName);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Observer process did not start.");
        try
        {
            var client = new NamedPipeAgentBridgeClient(pipeName);
            var receiver = new AgentRuntimeObservationReceiver(client);
            var received = new List<RuntimeObservedEvent>();
            for (var i = 0; i < 20; i++)
            {
                received.Add(await receiver.ReceiveAsync());
                if (received[^1].EventType == "ObserverStopped") break;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync(deadline.Token);
            check(child.ExitCode == 0 && received.First().EventType == "ObserverStarted" &&
                received.Last().EventType == "ObserverStopped", "independent observer process starts and stops");
            check(received.Any(x => x.EventType == "TargetRunning" &&
                x.Metadata.TryGetValue("ProcessId", out var pid) && pid == parentPid),
                "independent observer detects the live target process");
            check(received.All(x => x.IdentityStatus == "Observed" && x.Metadata.Keys.All(k =>
                    k is "ProcessName" or "ProcessId")),
                "Agent Bridge client receives only Observed process metadata");
            check(received.Count == client.Logs.Count(x => x.Stage == "EventReceived"),
                "Agent Bridge receives every event from the external observer process");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }
}
