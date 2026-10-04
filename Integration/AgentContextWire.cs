using System;
using System.Collections.Generic;

namespace QnocrPoc.Integration.Wire
{
    // C# 7.3 JSON boundary; the project-owned MessageEnvelope contract is unchanged.
    public sealed class AgentContextWire
    {
        public string SessionId { get; set; }
        public string BuyerId { get; set; }
        public string BuyerDisplayName { get; set; }
        public BuyerIdentityWire Identity { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }
        public string ProductId { get; set; }
        public List<ProductReferenceWire> ProductReferences { get; set; }
        public List<MessageProductLinkWire> MessageProductLinks { get; set; }
        public string CaptureId { get; set; }
        public string MessageId { get; set; }
        public string AiRequestId { get; set; }
        public int ModelInputMessageCount { get; set; }
        // Optional Agent Preview metadata; old Shadow contexts leave these unset.
        public string SellerId { get; set; }
        public string SessionStatus { get; set; }
        public string IntentName { get; set; }
        public double? IntentConfidence { get; set; }
        public string IntentProvider { get; set; }
        public string KnowledgeId { get; set; }
        public string KnowledgeReference { get; set; }
        public List<EnvelopeMessage> Messages { get; set; }
        public KnowledgeContextWire Knowledge { get; set; }
        public BoundProductContextWire CurrentProductContext { get; set; }
        public string ProductContextBindingStatus { get; set; }
    }

    public sealed class BoundProductContextWire
    {
        public string SessionId { get; set; }
        public string SellerId { get; set; }
        public string ProductId { get; set; }
        public string ProductName { get; set; }
        public string Source { get; set; }
        public double? Confidence { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public string BoundBy { get; set; }
        public string CaptureId { get; set; }
    }
    public sealed class KnowledgeContextWire
    {
        public string Status { get; set; }
        public string ProductId { get; set; }
        public string Source { get; set; }
        public Dictionary<string, string> FactFields { get; set; }
        public List<string> MissingFields { get; set; }
        public bool IsTestData { get; set; }
        public string UpdatedAt { get; set; }
        public string SellerId { get; set; }
        public string ProductVersion { get; set; }
        public bool MissingProduct { get; set; }
        public string KnowledgeCategory { get; set; }
    }
    public sealed class BuyerIdentityWire
    {
        public string BuyerId { get; set; }
        public string Source { get; set; }
        public double? Confidence { get; set; }
        public string CapturedAt { get; set; }
        public string SessionId { get; set; }
        public string Status { get; set; }
        public string BuyerDisplayName { get; set; }
        public string PlatformConversationId { get; set; }
        public string EvidenceId { get; set; }
        public bool IsTest { get; set; }
        public string VerifiedAt { get; set; }
    }
    public sealed class ProductReferenceWire
    {
        public string ProductId { get; set; }
        public string Source { get; set; }
        public string CapturedAt { get; set; }
        public string CaptureId { get; set; }
        public string AssociationStatus { get; set; }
    }

    public sealed class MessageProductLinkWire
    {
        public string MessageId { get; set; }
        public string ProductId { get; set; }
        public string CaptureId { get; set; }
        public string CapturedAt { get; set; }
        public string AssociationStatus { get; set; }
    }
}
