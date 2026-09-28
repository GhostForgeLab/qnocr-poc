using System.Collections.Concurrent;
using System.IO.Pipes;

namespace QnocrPoc.Extension.AgentBridge;

/// <summary>Test-only stand-in for a read-only qnbot Observer. One event per connection.</summary>
public sealed class NamedPipeAgentBridgeServer
{
    private readonly string _pipeName;
    private readonly ConcurrentQueue<AgentBridgeLogEntry> _logs = new();

    public NamedPipeAgentBridgeServer(string pipeName)
    {
        AgentBridgeWire.ValidatePipeName(pipeName);
        _pipeName = pipeName;
    }

    public IReadOnlyList<AgentBridgeLogEntry> Logs => _logs.ToArray();

    public async Task SendAsync(AgentEventEnvelope observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.ValidateForTransport();
        using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _logs.Enqueue(new(DateTimeOffset.UtcNow, "ServerStarted", observation.EventType, observation.SessionId));
        await pipe.WaitForConnectionAsync(cancellationToken);
        _logs.Enqueue(new(DateTimeOffset.UtcNow, "ClientConnected", observation.EventType, observation.SessionId));
        await AgentBridgeWire.WriteAsync(pipe, observation, cancellationToken);
        _logs.Enqueue(new(DateTimeOffset.UtcNow, "EventSent", observation.EventType, observation.SessionId));
    }
}
