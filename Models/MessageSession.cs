namespace QnocrPoc.Models;

public sealed record MessageSession(string SessionId, string SellerId, string BuyerId, string ObservedBuyer,
    string IdentityBinding, string ProductId, string? IdentityPath);
public sealed record MessageLifecycle(DateTimeOffset CaptureTime, string Source, string Buyer, string ProductId,
    string SessionId, string MessageId, string Fingerprint, string Stage, string AdapterResult, string DispatchResult,
    string AiState, int Attempt, string? Detail = null);
