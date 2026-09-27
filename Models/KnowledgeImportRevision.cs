using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

/// <summary>Transient, one-product test import. RawData is never written to audit logs.</summary>
public sealed record KnowledgeImportRequest(string ImportId, string SellerId, string ProductId,
    KnowledgeSource Source, string RawData, DateTimeOffset CreatedAt);

/// <summary>One bounded JSON product supplied by an external test source.</summary>
public sealed record KnowledgeImportPayload(string Name, string Version, bool IsTestData,
    string? Material = null, string? Capacity = null, string? Features = null);

public sealed record KnowledgeFieldChange(string Key, string OldValue, string NewValue, string Reason);

[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeRevisionStatus>))]
public enum KnowledgeRevisionStatus { Draft, Reviewing, Approved, Rejected, Conflict, Published }

/// <summary>Explicit single-field candidate based on one active published version.</summary>
public sealed record KnowledgeRevision(string RevisionId, string SellerId, string ProductId,
    string BaseVersion, IReadOnlyList<KnowledgeFieldChange> ChangedFields,
    DateTimeOffset CreatedAt, KnowledgeRevisionStatus Status,
    string BaseKnowledgeId, string? CandidateKnowledgeId, string CandidateVersion,
    KnowledgeSource Source);
