using System.Collections.Generic;

namespace QnocrPoc.Integration.Wire
{
    // C# 7.3 JSON boundary; the project-owned MessageEnvelope contract is unchanged.
    public sealed class AgentContextWire
    {
        public string SessionId { get; set; }
        public string BuyerId { get; set; }
        public string ProductId { get; set; }
        public string CaptureId { get; set; }
        public string MessageId { get; set; }
        public string AiRequestId { get; set; }
        public int ModelInputMessageCount { get; set; }
        public List<EnvelopeMessage> Messages { get; set; }
    }
}
