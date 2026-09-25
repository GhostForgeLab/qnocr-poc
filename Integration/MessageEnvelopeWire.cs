using System.Collections.Generic;

namespace QnocrPoc.Integration.Wire
{
    // JSON boundary DTO, C# 7.3/.NET 4.8 compatible. Not a reference to the .NET 8 OCR application.
    public sealed class MessageEnvelope
    {
        public int SchemaVersion { get; set; }
        public string Source { get; set; }
        public string SellerId { get; set; }
        public string BuyerId { get; set; }
        public string ProductId { get; set; }
        public string ProductBinding { get; set; }
        public List<EnvelopeMessage> Messages { get; set; }
    }
    public sealed class EnvelopeMessage
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public string Timestamp { get; set; }
    }
}
