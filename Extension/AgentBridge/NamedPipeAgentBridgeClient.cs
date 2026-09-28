using System.Collections.Concurrent;
using System.IO.Pipes;

namespace QnocrPoc.Extension.AgentBridge;

/// <summary>Agent-side transport reader. It does not invoke the Agent pipeline.</summary>
public sealed class NamedPipeAgentBridgeClient : IAgentBridge
{
    private readonly string _pipeName;
    private readonly ConcurrentQueue<AgentBridgeLogEntry> _logs = new();

    public NamedPipeAgentBridgeClient(string pipeName)
    {
        AgentBridgeWire.ValidatePipeName(pipeName);
        _pipeName = pipeName;
    }

    public IReadOnlyList<AgentBridgeLogEntry> Logs => _logs.ToArray();

    public async Task<AgentEventEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000, deadline.Token);
        _logs.Enqueue(new(DateTimeOffset.UtcNow, "ClientConnected", "", ""));
        var observation = await AgentBridgeWire.ReadAsync(pipe, deadline.Token);
        _logs.Enqueue(new(DateTimeOffset.UtcNow, "EventReceived", observation.EventType, observation.SessionId));
        return observation;
    }
}
