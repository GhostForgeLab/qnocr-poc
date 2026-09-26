using System.Text.Json;

namespace QnocrPoc.Shadow;

public static class EvaluationBatchCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Usage(); return 2; }
        var create = args[0] == "--assist-batch-start";
        var offset = create ? 1 : 2;
        if ((args.Length - offset) % 2 != 0) { Usage(); return 2; }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = offset; i < args.Length; i += 2)
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1])
                || string.IsNullOrWhiteSpace(args[i + 1])) { Usage(); return 2; }
        if (!options.TryGetValue("--state-dir", out var stateDir)) { Usage(); return 2; }
        var service = new EvaluationBatchService(stateDir);
        object result;
        if (create && options.Count == 4 && options.TryGetValue("--run-dir", out var run)
            && options.TryGetValue("--environment", out var environment)
            && options.TryGetValue("--operator", out var starter))
            result = service.Start(run, environment, starter);
        else if (args[0] == "--assist-batch-end" && options.Count == 2
            && options.TryGetValue("--operator", out var closer))
            result = service.End(args[1], closer);
        else if (args[0] == "--assist-batch-report" && options.Count == 1)
            result = service.Report(args[1]);
        else if (args[0] == "--assist-batch-feedback" && options.Count is >= 5 and <= 10
            && options.TryGetValue("--preview-id", out var previewId)
            && options.TryGetValue("--operator", out var reviewer)
            && options.TryGetValue("--type", out var feedbackType)
            && options.TryGetValue("--category", out var category))
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "--state-dir", "--preview-id", "--operator", "--type", "--category", "--reason",
                "--final-text-file", "--product-assessment", "--context-assessment", "--note-file" };
            if (options.Keys.Any(key => !allowed.Contains(key))) { Usage(); return 2; }
            var final = options.TryGetValue("--final-text-file", out var file) ? File.ReadAllText(file) : null;
            var note = options.TryGetValue("--note-file", out var noteFile) ? File.ReadAllText(noteFile) : null;
            result = service.RecordFeedback(args[1], previewId, reviewer, feedbackType, category,
                options.GetValueOrDefault("--reason"), final,
                options.GetValueOrDefault("--product-assessment", "Unknown"),
                options.GetValueOrDefault("--context-assessment", "Unknown"), note);
        }
        else { Usage(); return 2; }
        Console.WriteLine(JsonSerializer.Serialize(new { Result = result, SentByAssistant = 0,
            Note = "Evaluation metadata only; no AI call, training or send operation." },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: --assist-batch-start --state-dir DIR --run-dir RUN --environment RealQianniuShadow|HistoricalReview|IsolatedFixture --operator NAME | " +
        "--assist-batch-end ID --state-dir DIR --operator NAME | --assist-batch-report ID --state-dir DIR | " +
        "--assist-batch-feedback ID --state-dir DIR --preview-id PREVIEW --operator NAME --type Accepted|Edited|Rejected " +
        "--category 商品规格|材质|价格|发货|售后|其他 [--reason 信息不准确|语气问题|缺少商品信息|其他] " +
        "[--final-text-file FILE] [--note-file FILE] [--product-assessment Unknown|Correct|Incorrect] " +
        "[--context-assessment Unknown|Continued|Lost]. No send operation.");
}
