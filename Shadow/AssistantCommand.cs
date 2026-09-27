using System.Text.Json;

namespace QnocrPoc.Shadow;

public static class AssistantCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--assist-list" && args[1] == "--state-dir")
        {
            var views = new AssistantReplyService(args[2]).List();
            Console.WriteLine(JsonSerializer.Serialize(new { Previews = views.Select(v => new
            {
                v.ReplyPreviewId, v.SessionId, v.BuyerId, v.BuyerDisplayName, v.BuyerIdentityStatus,
                v.OriginalMessage, v.CreatedAt, v.RiskLevel, v.Status, v.SendStatus
            }), SentMessages = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length == 5 && args[0] is "--assist-dashboard" or "--assist-stats" or "--assist-sessions" or "--assist-samples" or "--assist-classifications"
            && args[1] == "--state-dir" && args[3] == "--run-dir")
        {
            var dashboard = new AssistantRuntimeDashboard(args[2], args[4]);
            object result = args[0] switch
            {
                "--assist-stats" => dashboard.Snapshot(),
                "--assist-sessions" => dashboard.Sessions(),
                "--assist-samples" => dashboard.Samples(),
                "--assist-classifications" => dashboard.Classifications(),
                _ => new { Stats = dashboard.Snapshot(), Sessions = dashboard.Sessions(), Samples = dashboard.Samples() }
            };
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length < 4 || args.Length % 2 != 0) { Usage(); return 2; }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
            if (!options.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1])) { Usage(); return 2; }
        if (!options.TryGetValue("--state-dir", out var root)) { Usage(); return 2; }
        var service = new AssistantReplyService(root);
        if (args[0] == "--assist-feedback" && options.Count is >= 3 and <= 7
            && options.TryGetValue("--operator", out var reviewer)
            && options.TryGetValue("--type", out var feedbackType))
        {
            var valid = new HashSet<string>(StringComparer.Ordinal)
            { "--state-dir", "--operator", "--type", "--final-text-file", "--product-assessment", "--context-assessment", "--note-file" };
            if (options.Keys.Any(key => !valid.Contains(key))) { Usage(); return 2; }
            var finalText = options.TryGetValue("--final-text-file", out var finalPath) ? File.ReadAllText(finalPath) : null;
            var note = options.TryGetValue("--note-file", out var noteFile) ? File.ReadAllText(noteFile) : null;
            var feedback = service.RecordFeedback(args[1], reviewer, feedbackType, finalText,
                options.GetValueOrDefault("--product-assessment", "Unknown"),
                options.GetValueOrDefault("--context-assessment", "Unknown"), operatorNote: note);
            Console.WriteLine(JsonSerializer.Serialize(new { Feedback = feedback, SentMessages = 0,
                Note = "Human assessment only; no model training or send action." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args[0] == "--assist-classify" && options.Count == 1)
        {
            var classification = service.ClassifyExisting(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new { Classification = classification, SentMessages = 0,
                Note = "Rule-based routing only; no knowledge, Prompt, model, or send mutation." },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var id = args[1];
        QnocrPoc.Models.AssistedReplyView? view;
        switch (args[0])
        {
            case "--assist-show" when options.Count == 1:
                view = service.Show(id);
                break;
            case "--assist-edit" when options.Count == 3 && options.TryGetValue("--operator", out var editor)
                && options.TryGetValue("--text-file", out var textFile):
                view = service.Edit(id, editor, File.ReadAllText(textFile));
                break;
            case "--assist-approve" when options.Count == 3 && options.TryGetValue("--operator", out var approver)
                && options.TryGetValue("--reply-sha256", out var approvedHash):
                view = service.Approve(id, approver, approvedHash);
                break;
            case "--assist-copy" when options.Count == 3 && options.TryGetValue("--operator", out var copier)
                && options.TryGetValue("--reply-sha256", out var copiedHash):
                view = service.Copy(id, copier, copiedHash, new WindowsAssistantClipboard());
                break;
            case "--assist-outcome" when options.Count == 3 && options.TryGetValue("--operator", out var reporter)
                && options.TryGetValue("--result", out var outcome):
                view = service.RecordManualOutcome(id, reporter, outcome);
                break;
            default: Usage(); return 2;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { View = view, SentMessages = 0,
            Note = "Clipboard only. Paste and send in Qianniu manually." }, new JsonSerializerOptions { WriteIndented = true }));
        return view is null ? 3 : 0;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: --assist-dashboard|--assist-stats|--assist-sessions|--assist-samples|--assist-classifications --state-dir DIR --run-dir RUN | --assist-feedback ID --state-dir DIR --operator NAME --type Accepted|Edited|Rejected [--final-text-file FILE] [--note-file FILE] [--product-assessment Unknown|Correct|Incorrect] [--context-assessment Unknown|Continued|Lost] | --assist-classify ID --state-dir DIR | --assist-list --state-dir DIR | --assist-show ID --state-dir DIR | --assist-edit ID --state-dir DIR --operator NAME --text-file FILE | " +
        "--assist-approve ID --state-dir DIR --operator NAME --reply-sha256 SHA256 | " +
        "--assist-copy ID --state-dir DIR --operator NAME --reply-sha256 SHA256 | --assist-outcome ID --state-dir DIR --operator NAME --result PastedAndSentReported|NotSentReported. No send operation.");
}
