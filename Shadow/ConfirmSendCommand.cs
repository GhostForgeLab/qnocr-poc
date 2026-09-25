using System.Text.Json;

namespace QnocrPoc.Shadow;

public static class ConfirmSendCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4 || args.Length % 2 != 0)
        {
            Usage(); return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
            if (!options.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                Usage(); return 2;
            }
        if (!options.TryGetValue("--state-dir", out var root)) { Usage(); return 2; }
        var service = new PreviewConfirmationService(root);
        if (args[0] == "--preview" && options.Count == 1)
        {
            var view = service.Show(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Status = view?.Delivery.Status ?? "PREVIEW_NOT_FOUND",
                Preview = view?.Preview,
                ReplySha256 = view?.Delivery.ReplySha256,
                SendEnabled = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return view is null ? 3 : 0;
        }
        if (args[0] == "--confirm-send" && options.Count == 4
            && options.TryGetValue("--operator", out var operatorId)
            && options.TryGetValue("--reply-sha256", out var hash)
            && options.TryGetValue("--adapter", out var adapterMode) && adapterMode == "isolated")
        {
            var adapter = new IsolatedRecordingSendAdapter(Path.Combine(root, "isolated-send", args[1]));
            var outcome = await service.ConfirmSendAsync(args[1], operatorId, hash, adapter);
            Console.WriteLine(JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));
            return outcome.Status == "Sent" && outcome.SendResult?.ActualSent == false ? 0 : 3;
        }
        if (args[0] == "--cancel-preview" && options.Count == 2
            && options.TryGetValue("--operator", out var cancelledBy))
        {
            var outcome = service.Cancel(args[1], cancelledBy);
            Console.WriteLine(JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));
            return outcome.Status == "Cancelled" ? 0 : 3;
        }
        Usage(); return 2;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: --preview ID --state-dir DIR | --confirm-send ID --state-dir DIR --operator NAME " +
        "--reply-sha256 SHA256 --adapter isolated | --cancel-preview ID --state-dir DIR --operator NAME. " +
        "No live send adapter is available from this CLI.");
}
