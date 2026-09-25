using System.Runtime.CompilerServices;
using QnocrPoc.Models;

namespace QnocrPoc.Adapter;

/// <summary>Adapts one confirmed OCR batch per enumeration. Does not capture, run OCR, infer identities, or send events.</summary>
public sealed class OcrMessageSource : IMessageSource
{
    private readonly Func<CancellationToken, Task<BuyerMessageBatch>> _readBatch;
    private readonly string _sellerId;
    private readonly string _buyerId;
    public string Source => "Qianniu.OCR";

    public OcrMessageSource(Func<CancellationToken, Task<BuyerMessageBatch>> readBatch, string sellerId = "", string buyerId = "")
    {
        ArgumentNullException.ThrowIfNull(readBatch);
        ValidateIdentity(sellerId, nameof(sellerId));
        ValidateIdentity(buyerId, nameof(buyerId));
        _readBatch = readBatch;
        _sellerId = sellerId;
        _buyerId = buyerId;
    }

    public async IAsyncEnumerable<MessageEnvelope> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = await _readBatch(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("OCR batch is null.");
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Messages is null) throw new InvalidDataException("OCR batch Messages is missing.");

        // Validate and snapshot the entire batch before yielding anything. ReviewItems are never considered.
        var events = new List<MessageEnvelope>();
        var current = new List<EnvelopeMessage>();
        string? currentProduct = null;
        var productBinding = string.IsNullOrWhiteSpace(batch.ProductBinding) ? "UnverifiedOcrContext" : batch.ProductBinding;
        foreach (var message in batch.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message is null || message.Role != "buyer" || message.Source != "Qianniu.PaddleOCR" ||
                string.IsNullOrWhiteSpace(message.Text) || message.Timestamp == default || message.ProductId is null ||
                message.ProductId != "" && !QnocrPoc.Product.ProductIdExtractor.IsValidProductId(message.ProductId))
                throw new InvalidDataException("Invalid confirmed BuyerMessage: expected buyer role, OCR source, nonempty text, timestamp and valid/empty product ID.");
            if (current.Count > 0 && currentProduct != message.ProductId)
            {
                events.Add(Create(currentProduct!, current, productBinding));
                current = new();
            }
            currentProduct = message.ProductId;
            current.Add(new(message.Role, message.Text, message.Timestamp));
        }
        if (current.Count > 0) events.Add(Create(currentProduct!, current, productBinding));
        foreach (var envelope in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return envelope;
        }
    }

    private MessageEnvelope Create(string productId, List<EnvelopeMessage> messages, string productBinding) =>
        new(Source, _sellerId, _buyerId, productId, messages.AsReadOnly())
        {
            ProductBinding = productId == "" ? "None" : productBinding == "None" ? "UnverifiedOcrContext" : productBinding
        };

    private static void ValidateIdentity(string value, string name)
    {
        if (value is null || value.Length > 0 && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Provide an explicit ID or an empty string for unknown identity.", name);
    }
}
