namespace QnocrPoc.Models;

/// <summary>Provider routing metadata only; never includes buyer text, prompt or API credentials.</summary>
public sealed record IntentProviderTrace(string Event, string Provider, string? Intent,
    bool FallbackUsed, DateTimeOffset Timestamp, string? SessionId = null,
    string? MessageId = null, string? CaptureId = null, string? Reason = null,
    double? LatencyMilliseconds = null);
