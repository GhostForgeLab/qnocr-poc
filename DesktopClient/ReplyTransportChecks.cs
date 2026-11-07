using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QNAgent;

internal static class ReplyTransportChecks
{
    internal static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        async Task<(string? Reply, ReplyCallTrace Trace, List<int> Budgets, string? Error)> Scenario(params (int Status, string Body)[] responses)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            var budgets = new List<int>();
            var server = Task.Run(async () =>
            {
                foreach (var response in responses)
                {
                    var incoming = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(6));
                    using var reader = new StreamReader(incoming.Request.InputStream);
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync()); budgets.Add(body.RootElement.GetProperty("max_tokens").GetInt32());
                    incoming.Response.StatusCode = response.Status;
                    await incoming.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response.Body)); incoming.Response.Close();
                }
            });
            var calls = new ReplyCallTrace(); string? reply = null, error = null;
            using var timeout = new CancellationTokenSource(8000);
            try { reply = await Connections.ChatAsync(new ClientSettings { AiEndpoint = $"http://127.0.0.1:{port}", AiModel = "fixture" }, "comparison fixture", timeout.Token, calls: calls); }
            catch (ReplyModelException ex) { error = ex.Code; }
            await server; return (reply, calls, budgets, error);
        }
        const string ok = "{\"model\":\"fixture-provider\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"大款更厚，小款更薄。\"}}]}";
        var limited = await Scenario((200, "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"未完成的片段\",\"reasoning\":\"never expose this\"}}]}"), (200, ok));
        Check(limited.Reply == "大款更厚，小款更薄。" && limited.Budgets[0] == 2048 && limited.Budgets[1] == 4096 && limited.Trace.RetryReasons.Contains("OutputTruncated"), "truncated output is discarded and retried with answer budget");
        var empty = await Scenario((200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null,\"reasoning\":\"not a customer answer\"}}]}"), (200, ok));
        Check(empty.Trace.ReplyRequestsAttempted == 2 && empty.Trace.ReplyRequestsSucceeded == 1 && empty.Reply == "大款更厚，小款更薄。", "empty final content is retried without using reasoning as an answer");
        var failed = await Scenario((200, "{\"choices\":[{\"message\":{\"content\":\"\"}}]}"), (200, "{\"choices\":[{\"message\":{\"content\":\"\"}}]}"));
        Check(failed.Error == "EmptyReply" && failed.Trace.ReplyRequestsAttempted == 2 && failed.Trace.ReplyHttpResponses == 2 && failed.Trace.ReplyRequestsSucceeded == 0, "repeated empty output has a precise bounded failure");
        var unauthorized = await Scenario((401, "{}"));
        Check(unauthorized.Error == "HTTP_401" && unauthorized.Trace.ReplyRequestsAttempted == 1, "authentication failures do not trigger blind retries");
        var transient = await Scenario((503, "{}"), (200, ok));
        Check(transient.Reply is not null && transient.Trace.RetryReasons.Contains("HTTP_503") && transient.Trace.ActualReplyModel == "fixture-provider", "transient service error retries once and records returned model");
        var array = await Scenario((200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"回复正文\"},{\"type\":\"reasoning\",\"text\":\"不要展示\"}]}}]}"));
        Check(array.Reply == "回复正文", "compatible text blocks are parsed without hidden reasoning blocks");
        Console.WriteLine($"Passed {count} reply transport checks. No external service called."); return 0;
    }
}
