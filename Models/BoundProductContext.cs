using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ProductContextSource>))]
public enum ProductContextSource { OCR, UI, Manual }

[JsonConverter(typeof(JsonStringEnumConverter<ProductContextBindingStatus>))]
public enum ProductContextBindingStatus { Unknown, Bound, Uncertain, Conflict }

/// <summary>Current per-session binding, separate from the OCR/catalog candidate ProductContext.</summary>
public sealed record BoundProductContext(string SessionId, string SellerId, string ProductId,
    string ProductName, ProductContextSource Source, double? Confidence,
    DateTimeOffset CapturedAt, string BoundBy, string? CaptureId = null);

public sealed record ProductContextResolution(BoundProductContext? Current,
    ProductContextBindingStatus Status, string Reason);
