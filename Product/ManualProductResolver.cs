using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QnocrPoc.Product;

/// <summary>Explicit, seller-scoped operator choice. OCR absence never supplies or changes this choice.</summary>
public sealed class ManualProductResolver(string stateRoot) : IProductContextResolver
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(2);

    public BoundProductContext Bind(string sessionId, string sellerId, string productId,
        string productName, string operatorId, DateTimeOffset? capturedAt = null)
    {
        ValidateSessionId(sessionId);
        if (!ProductIdExtractor.IsValidProductId(productId) || string.IsNullOrWhiteSpace(sellerId) ||
            string.IsNullOrWhiteSpace(productName) || productName.Length > 100 ||
            productName.Any(char.IsControl) || string.IsNullOrWhiteSpace(operatorId) || operatorId.Length > 128)
            throw new ArgumentException("Manual binding needs seller, valid product, name and operator.");
        var sessionPath = SessionPath(sessionId);
        var session = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(sessionPath))
            ?? throw new InvalidDataException("Session is unreadable.");
        if (session.SessionId != sessionId || session.SellerId != sellerId)
            throw new InvalidDataException("Seller or SessionId differs from the current session.");
        var path = BindingPath(sessionId);
        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<BoundProductContext>(File.ReadAllText(path));
            if (existing?.ProductId != productId)
                throw new InvalidOperationException("Clear the prior manual product before selecting another.");
        }
        var bound = new BoundProductContext(sessionId, sellerId, productId, productName,
            ProductContextSource.Manual, null, capturedAt ?? DateTimeOffset.UtcNow, operatorId);
        AtomicJson.Write(path, bound);
        RunLogWriter.Append(_root, "product-context-audit.jsonl", new
        {
            Event = "PRODUCT_CONTEXT_BOUND", bound.SessionId, bound.SellerId, bound.ProductId,
            bound.Source, bound.Confidence, Timestamp = bound.CapturedAt, bound.BoundBy
        });
        return bound;
    }

    public void Clear(string sessionId, string operatorId)
    {
        ValidateSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(operatorId)) throw new ArgumentException("Operator is required.");
        var path = BindingPath(sessionId);
        if (!File.Exists(path)) throw new FileNotFoundException("Manual binding not found.", path);
        var old = JsonSerializer.Deserialize<BoundProductContext>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Manual binding is unreadable.");
        File.Delete(path);
        RunLogWriter.Append(_root, "product-context-audit.jsonl", new
        {
            Event = "PRODUCT_CONTEXT_CLEARED", old.SessionId, old.SellerId, old.ProductId,
            old.Source, Timestamp = DateTimeOffset.UtcNow, BoundBy = operatorId
        });
    }

    public BoundProductContext? Resolve(MessageSession session, OcrResult? productOcr,
        DateTimeOffset capturedAt, string captureId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSessionId(session.SessionId);
        var path = BindingPath(session.SessionId);
        if (!File.Exists(path)) return null;
        var bound = JsonSerializer.Deserialize<BoundProductContext>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Manual product binding is unreadable.");
        if (bound.SessionId != session.SessionId || bound.SellerId != session.SellerId ||
            !ProductIdExtractor.IsValidProductId(bound.ProductId) || bound.Source != ProductContextSource.Manual ||
            bound.CapturedAt > capturedAt.AddMinutes(2) || capturedAt - bound.CapturedAt > MaximumAge)
            return null;
        return bound;
    }

    private string BindingPath(string sessionId) => Path.Combine(_root, "sessions", sessionId, "manual-product-binding.json");
    private string SessionPath(string sessionId) => Path.Combine(_root, "sessions", sessionId, "session.json");
    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length != 64 || !sessionId.All(Uri.IsHexDigit))
            throw new ArgumentException("SessionId must be 64 hexadecimal characters.");
    }
}
