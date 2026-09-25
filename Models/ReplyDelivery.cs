namespace QnocrPoc.Models;

public static class ReplyDeliveryStatuses
{
    public const string Created = "Created";
    public const string WaitingConfirmation = "WaitingConfirmation";
    public const string Confirmed = "Confirmed";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record ReplyDeliveryState(string ReplyPreviewId, string Status, DateTimeOffset UpdatedAt,
    string ReplySha256, string? ConfirmedBy = null, DateTimeOffset? ConfirmedAt = null,
    string? SendRequestId = null, string? AdapterMode = null, string? ResultStatus = null,
    bool ActualSent = false, string? HostUser = null);

public sealed record ConfirmedSendRequest(string SendRequestId, string ReplyPreviewId, string CaptureId,
    string SessionId, string BuyerId, string AiRequestId, string AiResponseId, string Reply,
    string ReplySha256, string ConfirmedBy, DateTimeOffset ConfirmedAt, string HostUser);

public sealed record SendAdapterResult(string Status, bool Acknowledged, bool ActualSent,
    string? Detail, DateTimeOffset CompletedAt);

public sealed record SendAuditEvent(DateTimeOffset At, string Event, string ReplyPreviewId,
    string? CaptureId, string? SessionId, string? AiRequestId, string? AiResponseId,
    string? SendRequestId, string? ConfirmedBy, string? ReplySha256, string? AdapterMode,
    string? ResultStatus, bool ActualSent, string? Detail, string? HostUser = null);
