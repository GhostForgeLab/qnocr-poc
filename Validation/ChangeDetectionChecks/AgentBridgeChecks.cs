using System.IO.Pipes;
using QnocrPoc.Extension.AgentBridge;

internal static class AgentBridgeChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var pipeName = "qn_agent_v1321_" + Guid.NewGuid().ToString("N");
        var observed = new AgentEventEnvelope(AgentEventEnvelope.BuyerMessageObserved,
            "test-session", new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(8)),
            "Observed", "buyer-test", "测试消息", "");
        var server = new NamedPipeAgentBridgeServer(pipeName);
        IAgentBridge client = new NamedPipeAgentBridgeClient(pipeName);
        var sending = server.SendAsync(observed);
        var received = await client.ReceiveAsync();
        await sending;
        check(received == observed && received.SchemaVersion == 1 && received.ProductId == "",
            "simulated Observer event crosses the named pipe unchanged as a versioned Agent event");
        check(server.Logs.Select(x => x.Stage).SequenceEqual(["ServerStarted", "ClientConnected", "EventSent"]) &&
            ((NamedPipeAgentBridgeClient)client).Logs.Select(x => x.Stage).SequenceEqual(["ClientConnected", "EventReceived"]),
            "server start, client connection, send and receive are logged without event content");

        var invalidServer = new NamedPipeAgentBridgeServer("qn_agent_invalid_" + Guid.NewGuid().ToString("N"));
        try
        {
            await invalidServer.SendAsync(observed with { IdentityStatus = "Verified" });
            check(false, "simulated Observer cannot claim Verified identity");
        }
        catch (InvalidDataException)
        {
            check(invalidServer.Logs.Count == 0, "simulated Observer cannot claim Verified identity");
        }

        var malformedName = "qn_agent_bad_" + Guid.NewGuid().ToString("N");
        using (var rawServer = new NamedPipeServerStream(malformedName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            var waiting = rawServer.WaitForConnectionAsync();
            IAgentBridge guardedClient = new NamedPipeAgentBridgeClient(malformedName);
            var receiving = guardedClient.ReceiveAsync();
            await waiting;
            await rawServer.WriteAsync(BitConverter.GetBytes(16 * 1024 + 1));
            await rawServer.FlushAsync();
            try
            {
                await receiving;
                check(false, "oversize pipe frames are rejected before deserialization");
            }
            catch (InvalidDataException)
            {
                check(true, "oversize pipe frames are rejected before deserialization");
            }
        }

        var canceledServer = new NamedPipeAgentBridgeServer("qn_agent_cancel_" + Guid.NewGuid().ToString("N"));
        using (var cancel = new CancellationTokenSource())
        {
            var waiting = canceledServer.SendAsync(observed, cancel.Token);
            cancel.Cancel();
            try
            {
                await waiting;
                check(false, "canceled observer wait does not send an event");
            }
            catch (OperationCanceledException)
            {
                check(canceledServer.Logs.All(x => x.Stage != "EventSent"),
                    "canceled observer wait does not send an event");
            }
        }
    }
}
