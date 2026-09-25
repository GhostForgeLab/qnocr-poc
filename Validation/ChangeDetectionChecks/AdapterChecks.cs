using System.Text.Json;
using QnocrPoc.Adapter;
using QnocrPoc.Models;

internal static class AdapterChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var time = new DateTime(2026, 9, 24, 19, 51, 45, DateTimeKind.Unspecified);
        BuyerMessage Message(string text, string product = "") => new() { Text = text, Timestamp = time, ProductId = product };
        BuyerMessageBatch Batch(params BuyerMessage[] messages) => new("unread-image.png", "", messages, [],
            [new("REVIEW MUST NOT LEAK", "Unconfirmed", [99], new(0, 0, 1, 1))], "ExplicitCallerContextNotVerifiedMessageAssociation", "Displayed time, timezone unspecified");
        static async Task<List<MessageEnvelope>> Consume(IMessageSource source, CancellationToken token = default)
        {
            var envelopes = new List<MessageEnvelope>();
            await foreach (var envelope in source.ReadAsync(token)) envelopes.Add(envelope);
            return envelopes;
        }

        var providerCalls = 0;
        var input = Batch(Message("这个可以刻字吗\n保留换行", "1051087533910"), Message(" ok ", "1051087533910"));
        IMessageSource source = new OcrMessageSource(_ => { providerCalls++; return Task.FromResult(input); });
        var result = await Consume(source);
        check(providerCalls == 1 && result.Count == 1 && result[0].Messages.Count == 2, "interface consumer gets one envelope from one batch provider invocation");
        check(result[0].Source == "Qianniu.OCR" && source.Source == "Qianniu.OCR" && result[0].SchemaVersion == 1, "source and contract version are explicit");
        check(result[0].SellerId == "" && result[0].BuyerId == "", "unknown seller/buyer IDs stay empty rather than inferred from OCR");
        check(result[0].ProductId == "1051087533910" && result[0].ProductBinding == input.ProductBinding, "product ID and unverified association provenance are preserved");
        check(result[0].Messages[0].Text == input.Messages[0].Text && result[0].Messages[1].Text == " ok " &&
            result[0].Messages[0].Timestamp == time && result[0].Messages[0].Timestamp.Kind == DateTimeKind.Unspecified,
            "text, whitespace, message order and displayed timestamp remain unchanged");
        var wire = JsonSerializer.Serialize(result[0]);
        using var document = JsonDocument.Parse(wire);
        var consumer = JsonSerializer.Deserialize<ConsumerEnvelope>(wire)!;
        check(consumer.Source == "Qianniu.OCR" && consumer.Messages.Length == 2 && consumer.Messages[0].Role == "buyer" &&
            consumer.Messages[0].Timestamp == "2026-09-24T19:51:45", "independent JSON consumer reads stable field names and timestamp string");
        check(!wire.Contains("REVIEW MUST NOT LEAK") && !wire.Contains("unread-image.png") && !wire.Contains("Evidence"),
            "review items and screenshot/OCR internals do not leak into unified messages");
        check(document.RootElement.GetProperty("Messages")[0].GetProperty("Text").GetString() == input.Messages[0].Text,
            "JSON round-trip preserves Unicode and multiline body");
        using var schema = JsonDocument.Parse(File.ReadAllText("Adapter/message-envelope.schema.json"));
        var requiredFields = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
        check(document.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(requiredFields),
            "emitted root fields agree with the checked-in v1 schema (field-set check, not a full schema engine)");

        var identities = await Consume(new OcrMessageSource(_ => Task.FromResult(input), "seller-test", "buyer-test"));
        check(identities[0].SellerId == "seller-test" && identities[0].BuyerId == "buyer-test", "explicit caller identities are carried verbatim");
        var mixed = Batch(Message("A1", "1051087533910"), Message("unknown"), Message("B", "1051087533911"), Message("A2", "1051087533910"));
        var groups = await Consume(new OcrMessageSource(_ => Task.FromResult(mixed)));
        check(groups.Count == 4 && groups.SelectMany(e => e.Messages).Select(m => m.Text).SequenceEqual(new[] { "A1", "unknown", "B", "A2" }),
            "A/unknown/B/A product runs stay distinct without reordering or cross-product attribution");
        check(groups[1].ProductId == "" && groups[1].ProductBinding == "None", "unknown product is not filled from adjacent events");
        var duplicateText = await Consume(new OcrMessageSource(_ => Task.FromResult(Batch(Message("same"), Message("same")))));
        check(duplicateText.Single().Messages.Count == 2, "adapter does not silently deduplicate repeated text");
        check((await Consume(new OcrMessageSource(_ => Task.FromResult(Batch())))).Count == 0, "review-only/empty batch produces no empty event");

        async Task Reject(BuyerMessage bad, string reason)
        {
            var received = 0;
            try { await foreach (var ignored in new OcrMessageSource(_ => Task.FromResult(Batch(Message("valid", "1051087533910"), bad))).ReadAsync()) received++; }
            catch (InvalidDataException) { check(received == 0, reason); return; }
            check(false, reason);
        }
        await Reject(new BuyerMessage { Role = "seller", Text = "bad", Timestamp = time }, "invalid role rejects entire batch before any event is yielded");
        await Reject(new BuyerMessage { Text = " ", Timestamp = time }, "blank message body is rejected");
        await Reject(new BuyerMessage { Text = "bad", Timestamp = default }, "missing/default timestamp is not replaced by current time");
        await Reject(new BuyerMessage { Text = "bad", Timestamp = time, ProductId = "bad-id" }, "invalid OCR product context is rejected");
        await Reject(new BuyerMessage { Text = "bad", Timestamp = time, Source = "unrelated-source" }, "unrelated message source is not relabeled as Qianniu OCR");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledCalls = 0;
        try { await Consume(new OcrMessageSource(_ => { cancelledCalls++; return Task.FromResult(input); }), cancelled.Token); check(false, "pre-cancellation honored"); }
        catch (OperationCanceledException) { check(cancelledCalls == 0, "pre-cancellation prevents provider invocation"); }
        using var between = new CancellationTokenSource();
        var count = 0;
        try
        {
            await foreach (var ignored in new OcrMessageSource(_ => Task.FromResult(mixed)).ReadAsync(between.Token)) { count++; between.Cancel(); }
            check(false, "cancellation between envelopes honored");
        }
        catch (OperationCanceledException) { check(count == 1, "cancellation stops later envelopes without claiming atomic delivery"); }

        var mutable = new List<BuyerMessage> { Message("first", "1051087533910"), Message("second", "1051087533911") };
        await using var iterator = new OcrMessageSource(_ => Task.FromResult(input with { Messages = mutable })).ReadAsync().GetAsyncEnumerator();
        await iterator.MoveNextAsync();
        mutable[1] = Message("changed by producer");
        await iterator.MoveNextAsync();
        check(iterator.Current.Messages.Single().Text == "second", "adapter snapshots the batch before yielding to a consumer");
        try { _ = new OcrMessageSource(_ => Task.FromResult(input), " "); check(false, "whitespace identity rejected"); }
        catch (ArgumentException) { check(true, "whitespace identity is rejected instead of silently routed"); }
        try { await Consume(new OcrMessageSource(_ => throw new IOException("test upstream failure"))); check(false, "upstream failure propagated"); }
        catch (IOException) { check(true, "upstream error propagates without fabricating events"); }
    }

    private sealed record ConsumerEnvelope(string Source, string SellerId, string BuyerId, string ProductId, ConsumerMessage[] Messages);
    private sealed record ConsumerMessage(string Role, string Text, string Timestamp);
}
