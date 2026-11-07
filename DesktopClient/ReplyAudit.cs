using System;
using System.Collections.Generic;

namespace QNAgent;

internal sealed class ReplyCallTrace
{
    internal JevAssessment? AnalysisSnapshot { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public long ElapsedMilliseconds { get; set; }
    public int ContextMessageCount { get; set; }
    public int ContextSellerMessages { get; set; }
    public bool JevAnalysisAttempted { get; set; }
    public bool JevAnalysisSucceeded { get; set; }
    public string? JevModel { get; set; }
    public string? JevIntent { get; set; }
    public string? ReplyModel { get; set; }
    public int ReplyRequestsAttempted { get; set; }
    public int ReplyRequestsSucceeded { get; set; }
    public int ReplyHttpResponses { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? LastFinishReason { get; set; }
    public string? ActualReplyModel { get; set; }
    public string? ReplyFailureCode { get; set; }
    public string? ReplyFailureMessage { get; set; }
    public List<string> RetryReasons { get; set; } = new();
    public bool VerificationAttempted { get; set; }
    public bool VerificationSucceeded { get; set; }
}
internal sealed record ReplyAudit
{
    public string? PipelineVersion { get; init; }
    public string? Mode { get; init; }
    public JevAssessment? Assessment { get; init; }
    public JevVerification? Verification { get; init; }
    public string? Failure { get; init; }
    public string? RejectedDraft { get; init; }
    public bool Guarded { get; init; }
    public bool ModelUsed { get; init; }
    public string? EvidenceMethod { get; init; }
    public ReplyCallTrace? Calls { get; init; }
}
