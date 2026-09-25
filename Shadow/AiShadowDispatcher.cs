using System.Text.Json;
using System.Threading.Channels;

namespace QnocrPoc.Shadow;

/// <summary>A bounded local queue keeps slow AI generation from blocking image capture. No delivery consumer.</summary>
public sealed class AiShadowDispatcher
{
    private sealed record Work(string Envelope, string Identity, string Output);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(32);
    private readonly Task _worker;
    private readonly List<ShadowAdapterResult> _results = [];
    public AiShadowDispatcher(string session, CancellationToken token,
        Func<string, string?, string, CancellationToken, Task<ShadowAdapterResult>>? generate = null)
    {
        generate ??= new ShadowAdapterClient(aiShadow: true).ConvertAsync;
        _worker = Task.Run(async () =>
        {
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                ShadowAdapterResult result;
                try { token.ThrowIfCancellationRequested(); result = await generate(work.Envelope, work.Identity, work.Output, token); }
                catch (OperationCanceledException) { result = new("AiCancelled", 0, null, "Observation cancelled."); }
                catch (Exception ex) { result = new("AiFailed", 0, null, ex.GetType().Name + ": " + ex.Message); }
                _results.Add(result);
                var json = JsonSerializer.Serialize(new { EnvelopePath = work.Envelope, AiEnabled = true, SendEnabled = false, Result = result });
                await File.AppendAllTextAsync(Path.Combine(session, "ai-shadow.jsonl"), json + Environment.NewLine);
                Console.WriteLine($"[AI Response Preview] status={result.Status}; aiInvoked={result.AiInvoked}; send=OFF; receipt={result.ReceiptPath}");
                if (result.ReplySuggestion is not null) Console.WriteLine(result.ReplySuggestion);
            }
        });
    }
    public Task<ShadowAdapterResult> EnqueueAsync(string envelope, string? identity, string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity is null) throw new InvalidDataException("AI Shadow requires an explicit test identity mapping.");
        var accepted = _queue.Writer.TryWrite(new(envelope, identity, output));
        return Task.FromResult(new ShadowAdapterResult(accepted ? "AiQueued" : "AiQueueFull", 0, null,
            accepted ? "Queued for isolated generation; not yet an AI result." : "Queue full; envelope retained without retry."));
    }
    public async Task<IReadOnlyList<ShadowAdapterResult>> CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
        return _results.AsReadOnly();
    }
}
