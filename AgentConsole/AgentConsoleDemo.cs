using System.Text.Json;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;

internal static class AgentConsoleDemo
{
    public static async Task<(string State, string Run)> CreateAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Demo requires a new directory.");
        var run = Path.Combine(root, "run");
        var state = Path.Combine(root, "reply-state");
        var frames = Path.Combine(run, "frames", "0001");
        Directory.CreateDirectory(frames);
        var intent = Path.Combine(root, "intent.json");
        File.WriteAllText(intent, JsonSerializer.Serialize(new { Provider = "RuleBased", Fallback = "RuleBased", Enabled = true, JevConfigPath = "unused-demo.json" }));
        var workflow = Path.Combine(root, "knowledge-items.json");
        File.WriteAllText(workflow, "[]");
        File.WriteAllText(Path.Combine(run, "session.json"), JsonSerializer.Serialize(new
        {
            Mode = "ShadowReplyPreview", StateDirectory = state, SendEnabled = false,
            SentMessages = 0, EvidenceKind = "SyntheticDemo"
        }));
        using var pipeline = new AgentReplyPipeline(Path.Combine(root, "context.db"),
            Path.Combine(root, "product-state"), "Knowledge/product-knowledge.json", workflow, state,
            intentConfigPath: intent);
        string[] messages = ["这款是什么材质？", "有200ml的规格吗？"];
        for (var index = 0; index < messages.Length; index++)
        {
            var envelope = new MessageEnvelope("Qianniu.OCR", "seller-demo", "buyer-demo-" + (index + 1), "",
                [new EnvelopeMessage("buyer", messages[index], DateTime.Now.AddSeconds(index))]);
            var result = await pipeline.ProcessMessageAsync(envelope, captureId: frames);
            if (result.Results.Count != 1 || result.Results[0].Preview is null || pipeline.AiCalls != 0)
                throw new InvalidOperationException("Demo could not produce the expected local review preview.");
            File.WriteAllText(Path.Combine(frames, $"reply_{index + 1:D4}.json"), JsonSerializer.Serialize(result));
        }
        return (state, run);
    }
}
