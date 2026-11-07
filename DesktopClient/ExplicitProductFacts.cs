using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QNAgent;

internal static class ExplicitProductFacts
{
    // Only a last-resort verbatim fact quote after a model request. No fit/inner-size inference.
    internal static string? Answer(string evidence, string intent)
    {
        using var json = JsonDocument.Parse(evidence); var root = json.RootElement;
        if (!root.TryGetProperty("Product", out var product) || product.ValueKind != JsonValueKind.Object) return null;
        if (intent == "PRODUCT_IDENTITY_QUERY" && product.TryGetProperty("Fields", out var fields) &&
            fields.TryGetProperty("SalesScope", out var value) && value.GetString() is { Length: > 0 and <= 400 } scope && ReplyTextPolicy.Allowed(scope))
            return ReplyTextPolicy.Normalize(scope);
        var question = root.GetProperty("BuyerQuestion").GetString() ?? "";
        if (intent != "PRODUCT_SPEC_QUERY" || question.Length > 25 ||
            !Regex.IsMatch(question, "尺寸|多大|长宽高") || Regex.IsMatch(question, "内|放|装|适配|价格|钱|几个|多少个|盒|吗") ||
            !product.TryGetProperty("Variants", out var variants) || variants.ValueKind != JsonValueKind.Array) return null;
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variant in variants.EnumerateArray())
        {
            var details = variant.TryGetProperty("Details", out var d) ? d.GetString() ?? "" : "";
            var match = Regex.Match(details, @"^(?<label>[^:：\r\n]{0,16}尺寸)\s*[:：]\s*(?<size>\d+(?:\.\d+)?\s*[*×xX]\s*\d+(?:\.\d+)?(?:\s*[*×xX]\s*\d+(?:\.\d+)?)?\s*(?:mm|cm|毫米|厘米))\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) return null; // Do not cherry-pick or omit unknown variants.
            var label = match.Groups["label"].Value; var size = ReplyTextPolicy.Normalize(match.Groups["size"].Value);
            if (facts.TryGetValue(label, out var prior) && prior != size) return null;
            facts[label] = size;
        }
        if (facts.Count is 0 or > 6) return null;
        var answer = string.Join("；", facts.Select(pair => pair.Key + "：" + pair.Value)) + "。";
        return ReplyTextPolicy.Allowed(answer) ? answer : null;
    }
}
