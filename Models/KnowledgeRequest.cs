namespace QnocrPoc.Models;

/// <summary>Read-only knowledge need. ProductReference is only the currently observed ID, never a catalog entity.</summary>
public sealed record KnowledgeRequest(string SessionId, ProductReference? ProductReference,
    string Intent, string Query, IReadOnlyList<string> NeedFields,
    string MessageId, string CaptureId, DateTimeOffset CreatedAt, string SellerId = "");
