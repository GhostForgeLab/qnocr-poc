using System.Threading;
using System.Threading.Tasks;

namespace CompatibilityHost
{
    internal interface IAIProvider
    {
        AIProviderConfig Config { get; }
        void PrepareAgentPipeline();
        Task<string> GenerateAsync(string seller, string buyer, string question, CancellationToken token);
        Task<ProviderSmokeResult> SmokeAsync(string question);
    }

    internal sealed class ProviderSmokeResult
    {
        public string ProviderName { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
        public int HttpStatus { get; set; }
        public string Content { get; set; }
        public bool Success { get; set; }
        public bool SendEnabled { get { return false; } }
        public int SentMessages { get { return 0; } }
    }
}
