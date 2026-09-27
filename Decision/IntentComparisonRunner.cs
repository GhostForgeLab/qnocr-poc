using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Decision;

/// <summary>Offline RuleBased remains authoritative; JEV output is observation only.</summary>
public sealed class IntentComparisonRunner(IIntentClassifier ruleBased, IIntentClassifier jev)
{
    public IReadOnlyList<IntentComparison> Compare(IReadOnlyList<IntentSample> samples, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count is < 20 or > 100 || samples.Any(sample => string.IsNullOrWhiteSpace(sample.Id) ||
            string.IsNullOrWhiteSpace(sample.Message) || sample.Message.Length > 1000) ||
            samples.Select(sample => sample.Id).Distinct(StringComparer.Ordinal).Count() != samples.Count)
            throw new InvalidDataException("Comparison needs 20–100 distinct bounded synthetic questions.");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(Path.Combine(outputDirectory, "intent-comparison.jsonl")) ||
            File.Exists(Path.Combine(outputDirectory, "intent-comparison.json")))
            throw new InvalidOperationException("Comparison output already exists; use a new run directory.");
        var logPath = Path.Combine(outputDirectory, "intent-comparison.jsonl");
        var comparisons = new List<IntentComparison>(samples.Count);
        foreach (var sample in samples)
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sample.Id + sample.Message)));
            var context = new SessionContextView(new string('A', 64), "buyer-test", "",
                [new EnvelopeMessage("buyer", sample.Message, new DateTime(2026, 9, 27, 12, 0, 0))],
                DateTimeOffset.UtcNow, "jev-fixture:" + sample.Id, digest, new string('C', 64), 1);
            var rule = ruleBased.Classify(context);
            IntentResult? jevResult = null;
            string? error = null;
            try { jevResult = jev.Classify(context); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error = ex.GetType().Name;
            }
            var model = jevResult?.RuleVersion.StartsWith("JEV/", StringComparison.Ordinal) == true
                ? jevResult.RuleVersion[4..] : null;
            var match = jevResult is null ? (bool?)null : rule.IntentName == jevResult.IntentName;
            var comparison = new IntentComparison(sample.Id, sample.Message, sample.ExpectedIntent,
                rule.IntentName, jevResult?.IntentName, match,
                jevResult is null ? "JevUnavailable" : match == true ? "Same" : "Different",
                jevResult?.Confidence, model, DateTimeOffset.UtcNow, error);
            comparisons.Add(comparison);
            File.AppendAllText(logPath, JsonSerializer.Serialize(new
            {
                Event = "INTENT_COMPARISON", Input = sample.Message,
                RuleResult = rule.IntentName, JEVResult = jevResult?.IntentName,
                comparison.Match, comparison.Difference, comparison.JevConfidence,
                comparison.JevModel, comparison.JevError, comparison.Timestamp
            }) + Environment.NewLine);
        }
        File.WriteAllText(Path.Combine(outputDirectory, "intent-comparison.json"),
            JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true }));
        return comparisons;
    }
}
