using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace QNAgent;

internal sealed class ReplyModelException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

internal static class ReplyTransport
{
    internal static async Task<string> SendAsync(Uri endpoint, string key, string payload, CancellationToken token, ReplyCallTrace? calls,
        int maxReplyCharacters = 4000, int retryMaxTokens = 4096)
    {
        var body = JsonNode.Parse(payload)!.AsObject();
        // Reasoning can consume the completion allowance. Reserve enough budget for the answer
        // without ever reading, displaying or retaining the model's reasoning text.
        if (endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase))
            body["reasoning"] = new JsonObject { ["effort"] = "low", ["exclude"] = true };
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
                if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                if (calls is not null) { calls.ReplyRequestsAttempted++; calls.LastHttpStatus = null; calls.LastFinishReason = null; }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (calls is not null) { calls.LastHttpStatus = (int)response.StatusCode; calls.ReplyHttpResponses++; }
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    throw new ReplyModelException("HTTP_" + status, $"回复服务返回 HTTP {status}。", status is 408 or 429 or 500 or 502 or 503 or 504 or 529);
                }
                using var document = JsonDocument.Parse(await Connections.LimitedBody(response, 1024 * 1024, deadline.Token));
                var root = document.RootElement;
                if (calls is not null && root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                    calls.ActualReplyModel = model.GetString() is { Length: <= 200 } name ? name : null;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    throw new ReplyModelException("InvalidResponse", "回复服务没有返回有效的 choices。", true);
                var choice = choices[0];
                var finish = choice.TryGetProperty("finish_reason", out var end) && end.ValueKind == JsonValueKind.String ? end.GetString() : null;
                if (calls is not null) calls.LastFinishReason = finish is { Length: <= 80 } ? finish : null;
                if (finish == "length")
                {
                    body["max_tokens"] = retryMaxTokens;
                    throw new ReplyModelException("OutputTruncated", "模型输出达到长度上限，尚未得到完整回复。", true);
                }
                if (finish == "content_filter") throw new ReplyModelException("ContentFiltered", "回复被服务端过滤，请人工处理。", false);
                if (!choice.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                    throw new ReplyModelException("EmptyReply", "模型没有返回回复正文。", true);
                var reply = content.ValueKind == JsonValueKind.String ? content.GetString()
                    : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object &&
                        p.TryGetProperty("type", out var type) && type.GetString() == "text" && p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        .Select(p => p.GetProperty("text").GetString())) : null;
                if (string.IsNullOrWhiteSpace(reply)) throw new ReplyModelException("EmptyReply", "模型没有返回回复正文。", true);
                if (reply.Length > maxReplyCharacters) throw new ReplyModelException("ReplyTooLong", "回复正文过长，未作为建议展示。", false);
                if (calls is not null) { calls.ReplyRequestsSucceeded++; calls.ReplyFailureCode = null; calls.ReplyFailureMessage = null; }
                return key.Length == 0 ? reply : reply.Replace(key, "[REDACTED]", StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is ReplyModelException or HttpRequestException or OperationCanceledException or JsonException)
            {
                var failure = error as ReplyModelException ?? (error is OperationCanceledException
                    ? new ReplyModelException("Timeout", "回复服务超时。", true)
                    : error is JsonException ? new ReplyModelException("InvalidJson", "回复服务返回了无效的 JSON。", true)
                    : new ReplyModelException("NetworkFailure", "无法连接回复服务。", true));
                if (calls is not null) { calls.ReplyFailureCode = failure.Code; calls.ReplyFailureMessage = failure.Message; }
                if (attempt == 1 || !failure.Retryable) throw failure;
                calls?.RetryReasons.Add(failure.Code);
                await Task.Delay(400, token);
            }
        }
        throw new InvalidOperationException("Reply retry limit reached.");
    }
}
