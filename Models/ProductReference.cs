namespace QnocrPoc.Models;

/// <summary>A visible product ID only; it is not a verified message-to-product or catalog binding.</summary>
public sealed record ProductReference(string ProductId, string Source, DateTimeOffset CapturedAt,
    string CaptureId, string AssociationStatus);

/// <summary>Sidecar timeline relationship; the MessageEnvelope contract remains unchanged.</summary>
public sealed record MessageProductLink(string MessageId, string ProductId, string CaptureId,
    DateTimeOffset CapturedAt, string AssociationStatus);
