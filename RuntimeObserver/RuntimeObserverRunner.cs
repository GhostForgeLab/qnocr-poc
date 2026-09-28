using QnocrPoc.Extension.AgentBridge;

namespace QnocrPoc.RuntimeObserver;

/// <summary>Standalone polling loop. The only output is the existing Agent named pipe.</summary>
public static class RuntimeObserverRunner
{
    public static async Task RunAsync(string processName, string pipeName, int polls,
        TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (polls < 1) throw new ArgumentOutOfRangeException(nameof(polls));
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var observer = new ExternalProcessObserver(processName);
        var bridge = new NamedPipeAgentBridgeServer(pipeName);
        for (var i = 0; i < polls; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var observation in observer.Poll())
                await bridge.SendAsync(observation.ToAgentEventEnvelope(), cancellationToken);
            if (i + 1 < polls) await Task.Delay(interval, cancellationToken);
        }
        await bridge.SendAsync(observer.Stop().ToAgentEventEnvelope(), cancellationToken);
    }
}
