using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class AgentPipelineChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV15", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string? HeaderBuyer(params string[] labels)
        {
            var lines = labels.Select((label, i) => new OcrLine(i + 1, label + " 2026-9-25 15:00:01", 0.95f,
                new OcrBounds(0, i * 40, 180, 20))).ToArray();
            var batch = new BuyerMessageBatch("fixture", "fixture", labels.Select((_, i) =>
                new BuyerMessage { Text = "question " + i, Timestamp = new DateTime(2026, 9, 25, 15, 0, i + 1) }).ToArray(),
                labels.Select((_, i) => new BuyerMessageEvidence(i, new OcrBounds(0, i * 40, 180, 30),
                    Array.Empty<int>(), new[] { i + 1 }, 0.95)).ToArray(), Array.Empty<MessageReviewItem>(), "", "");
            return SessionRegistry.ReadBuyer(batch, new OcrResult("fixture", "fixture", lines, 0, null, 0, 0, 0, 0, 0, 0));
        }
        check(HeaderBuyer("tb300116357", "tb3001163572", "tb300116357") == "tb300116357",
            "one trailing digit OCR outlier with two exact buyer anchors preserves session binding");
        check(HeaderBuyer("tb300116357", "other-buyer", "tb300116357") is null
            && HeaderBuyer("tb300116357", "tb3001163572") is null,
            "unrelated or two-way ambiguous buyer labels never bind a session");
        var sid = new string('A', 64);
        var session = new MessageSession(sid, "seller-test", "buyer-test:" + sid[..16], "synthetic-buyer", "IsolatedTestIdentity", "1051087533910", "test-mapping.json");
        var first = new DispatchItem(new string('1', 64), new string('2', 64), session,
            new("Qianniu.OCR", session.SellerId, session.BuyerId, "1051087533910", [new("buyer", "多少钱", new(2026, 9, 25, 10, 0, 1))]),
            DateTimeOffset.Now.AddSeconds(-10), false);
        var other = first with { MessageId = new string('3', 64), Session = session with { SessionId = new string('B', 64) } };
        var latest = first with { MessageId = new string('4', 64), AiEnabled = true, CaptureId = "fixture:frame_000002",
            AiRequestId = MessageFingerprint.AiRequestId(new string('4', 64)),
            Envelope = first.Envelope with { Messages = [new("buyer", "可以刻字吗", new(2026, 9, 25, 10, 0, 2))] } };
        var context = SessionContextBuilder.Build(latest, [first, other]);
        check(context.SessionId == sid && context.BuyerId == session.BuyerId && context.ProductId == "1051087533910"
            && context.Messages.Select(m => m.Text).SequenceEqual(new[] { "多少钱", "可以刻字吗" }), "context contains same-session history and current message, never another buyer");
        check(context.CaptureId == latest.CaptureId && context.AiRequestId == latest.AiRequestId && context.ModelInputMessageCount == 2,
            "capture, session and AI request IDs link without changing MessageEnvelope");
        check(context.BuyerDisplayName == "synthetic-buyer" && context.ProductReferences?.Single().ProductId == "1051087533910"
            && context.MessageProductLinks?.Count == 2 && context.MessageProductLinks.All(link => link.ProductId == "1051087533910"),
            "session context carries buyer label and per-message product reference sidecars");
        var trace = new AgentPipelineTrace(root);
        check(trace.MessageCaptured(latest), "captured event and session events persist before dispatch");
        var adapterCalls = 0;
        using (var box = new DurableShadowDispatch(Path.Combine(root, "success-state"), Path.Combine(root, "life.jsonl"), CancellationToken.None,
            (envelopePath, _, _, _) =>
            {
                adapterCalls++;
                var folder = Path.GetDirectoryName(envelopePath)!;
                var decision = JsonSerializer.Deserialize<IntentResult>(File.ReadAllText(Path.Combine(folder, "decision.json")));
                check(decision?.IntentName == "CUSTOMIZATION_QUERY" && decision.NeedProductContext && decision.NeedKnowledge,
                    "decision sidecar is durable before unchanged AI adapter invocation");
                using var knowledgeDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "knowledge-result.json")));
                check(knowledgeDoc.RootElement.GetProperty("Status").GetString() == KnowledgeStatuses.Missing &&
                    knowledgeDoc.RootElement.GetProperty("Source").GetString() == "None" &&
                    knowledgeDoc.RootElement.GetProperty("MissingFields")[0].GetString() == "CustomizationPolicy" &&
                    !knowledgeDoc.RootElement.TryGetProperty("Content", out _),
                    "EmptyKnowledgeProvider reports missing fields before unchanged AI adapter call without persisting content");
                check(!File.ReadAllText(Path.Combine(folder, "context.json")).Contains("\"Decision\"") &&
                    !File.ReadAllText(envelopePath).Contains("\"Decision\"") &&
                    !File.ReadAllText(Path.Combine(folder, "context.json")).Contains("\"Knowledge\"") &&
                    !File.ReadAllText(envelopePath).Contains("\"Knowledge\""),
                    "decision and knowledge metadata do not modify model context or MessageEnvelope input");
                return Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true,
                    "Synthetic preview; no provider call", 26, DateTimeOffset.Now, 1));
            }, true, trace))
        {
            check(box.Submit(latest, context) && File.Exists(Path.Combine(root, "success-state", "outbox", latest.MessageId, "context.json")),
                "session context is durably saved beside the one-message envelope");
            box.Start(); await box.StopAsync();
            check(adapterCalls == 1 && box.Snapshot().Single().State == "Completed" && box.Snapshot().Single().AiCalls == 1,
                "one simulated Agent response durably records one AI call count");
            var preview = JsonSerializer.Deserialize<ReplyPreview>(File.ReadAllText(Path.Combine(root, "success-state", "outbox", latest.MessageId, "reply-preview.json")))!;
            check(preview.SessionId == sid && preview.BuyerId == session.BuyerId && preview.Input.Messages.Count == 2 && preview.Confidence is null
                && preview.SendStatus == "Shadow" && preview.CaptureId == latest.CaptureId && preview.AiRequestId == latest.AiRequestId
                && preview.AiResponseId.Length > 0 && preview.ReplyPreviewId.Length > 0 &&
                preview.Decision?.IntentName == "CUSTOMIZATION_QUERY" && preview.Decision.RiskLevel == "Medium" &&
                preview.Intent == "CUSTOMIZATION_QUERY" && preview.RiskLevel == "Medium",
                "preview carries capture, request, response and preview IDs with fixed Shadow send status");
            var assistant = new AssistantReplyService(Path.Combine(root, "success-state")).Show(preview.ReplyPreviewId);
            check(assistant?.Status == AssistedReplyStatuses.AiGenerated && assistant.AiReply == preview.Reply
                && assistant.Decision?.IntentName == "CUSTOMIZATION_QUERY" && assistant.DecisionRiskLevel == "Medium" &&
                assistant.KnowledgeStatus == KnowledgeStatuses.Missing
                && File.Exists(Path.Combine(root, "success-state", "outbox", latest.MessageId, "assistant-reply.json")),
                "Agent preview automatically initializes durable assistant review without a send action");
        }
        var successEvents = File.ReadAllLines(Path.Combine(root, "agent-pipeline.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        check(successEvents.Select(e => e.GetProperty("Event").GetString()).SequenceEqual(new[]
            { "MESSAGE_CAPTURED", "SESSION_CREATED", "BUYER_BOUND", "PRODUCT_REFERENCE_BOUND", "MESSAGE_ATTACHED",
                "SESSION_UPDATED", "DECISION_CREATED", "KNOWLEDGE_REQUEST", "KNOWLEDGE_RESULT",
                "AI_REQUEST", "AI_RESPONSE", "REPLY_PREVIEW_CREATED" }),
            "one message follows the complete ordered Agent lifecycle");
        check(successEvents.All(e => e.GetProperty("CaptureId").GetString() == latest.CaptureId
            && e.GetProperty("SessionId").GetString() == sid && e.GetProperty("AiRequestId").GetString() == latest.AiRequestId),
            "capture, session and request IDs join every lifecycle row");
        var decisionEvent = successEvents.Single(e => e.GetProperty("Event").GetString() == "DECISION_CREATED");
        check(decisionEvent.GetProperty("Intent").GetString() == "CUSTOMIZATION_QUERY" &&
            decisionEvent.GetProperty("RiskLevel").GetString() == "Medium" &&
            decisionEvent.GetProperty("Confidence").ValueKind == JsonValueKind.Null &&
            decisionEvent.GetProperty("MessageId").GetString() == latest.MessageId,
            "DECISION_CREATED logs session/message, intent, uncalibrated confidence and risk before AI request");
        var knowledgeRequestEvent = successEvents.Single(e => e.GetProperty("Event").GetString() == "KNOWLEDGE_REQUEST");
        var knowledgeResultEvent = successEvents.Single(e => e.GetProperty("Event").GetString() == "KNOWLEDGE_RESULT");
        check(knowledgeRequestEvent.GetProperty("SessionId").GetString() == sid &&
            knowledgeRequestEvent.GetProperty("Intent").GetString() == "CUSTOMIZATION_QUERY" &&
            knowledgeRequestEvent.GetProperty("ProductReference").GetString() == "1051087533910" &&
            knowledgeRequestEvent.GetProperty("NeedFields")[0].GetString() == "CustomizationPolicy" &&
            knowledgeResultEvent.GetProperty("Success").GetBoolean() == false &&
            knowledgeResultEvent.GetProperty("Source").GetString() == "None" &&
            knowledgeResultEvent.GetProperty("MissingFields")[0].GetString() == "CustomizationPolicy",
            "knowledge trace records request identity/fields and missing result without buyer query or key");
        check(successEvents.Single(e => e.GetProperty("Event").GetString() == "AI_REQUEST").GetProperty("PromptLength").GetInt32() == 26,
            "AI_REQUEST logs actual reported model question length without API key");
        var responseId = successEvents.Single(e => e.GetProperty("Event").GetString() == "AI_RESPONSE")
            .GetProperty("AiResponseId").GetString();
        check(!string.IsNullOrEmpty(responseId) && successEvents.Single(e => e.GetProperty("Event").GetString() == "REPLY_PREVIEW_CREATED")
            .GetProperty("AiResponseId").GetString() == responseId,
            "AI response ID joins the response and preview audit records");
        check(!File.ReadAllText(Path.Combine(root, "agent-pipeline.jsonl")).Contains("ApiKey", StringComparison.OrdinalIgnoreCase) &&
            !File.ReadAllText(Path.Combine(root, "agent-pipeline.jsonl")).Contains("可以刻字吗", StringComparison.Ordinal),
            "lifecycle audit contains no key/configuration or buyer query text");

        var failure = latest with { MessageId = new string('5', 64), AiRequestId = MessageFingerprint.AiRequestId(new string('5', 64)) };
        using (var box = new DurableShadowDispatch(Path.Combine(root, "failure-state"), Path.Combine(root, "failure-life.jsonl"), CancellationToken.None,
            (_, _, _, _) => Task.FromResult(new ShadowAdapterResult("AiFailed", 0, null, "Simulated unavailable model", true)), true, trace))
        {
            box.Submit(failure, context with { MessageId = failure.MessageId, AiRequestId = failure.AiRequestId! });
            box.Start(); await box.StopAsync();
            check(box.Snapshot().Single().State == "AiFailed" && !File.Exists(Path.Combine(root, "failure-state", "outbox", failure.MessageId, "reply-preview.json")),
                "AI failure is held and cannot fabricate a reply preview");
        }
        var empty = latest with { MessageId = new string('6', 64), AiRequestId = MessageFingerprint.AiRequestId(new string('6', 64)) };
        using (var box = new DurableShadowDispatch(Path.Combine(root, "empty-state"), Path.Combine(root, "empty-life.jsonl"), CancellationToken.None,
            (_, _, _, _) => Task.FromResult(new ShadowAdapterResult("Converted", 1, null, null, true, "")), true, trace))
        {
            box.Submit(empty, context with { MessageId = empty.MessageId, AiRequestId = empty.AiRequestId! });
            box.Start(); await box.StopAsync();
            check(box.Snapshot().Single().State == "EmptyReply" && !File.Exists(Path.Combine(root, "empty-state", "outbox", empty.MessageId, "reply-preview.json")),
                "empty model output records EMPTY_REPLY and no preview");
        }
        trace.SessionMissing("fixture:frame_000003", "", 1);
        var allEvents = File.ReadAllLines(Path.Combine(root, "agent-pipeline.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.GetProperty("Event").GetString()).ToArray();
        check(allEvents.Contains("AI_ERROR") && allEvents.Contains("EMPTY_REPLY") && allEvents.Contains("SESSION_MISSING"),
            "AI failure, empty response and missing session are separate auditable states");
    }
}
