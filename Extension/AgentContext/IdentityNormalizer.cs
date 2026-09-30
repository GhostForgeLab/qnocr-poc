using System.Text;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Unicode compatibility normalization, whitespace removal, invariant case and trailing sentence punctuation.</summary>
public sealed class IdentityNormalizer : IIdentityNormalizer
{
    public string Normalize(string observedBuyerLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedBuyerLabel);
        var normalized = observedBuyerLabel.Normalize(NormalizationForm.FormKC);
        var label = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune)) label.Append(rune.ToString());

        // Preserve internal/leading punctuation and username characters such as '-' and '_'.
        // No substitutions between digits and letters, transliteration or fuzzy OCR correction.
        var result = label.ToString().TrimEnd('.', ',', '!', '?', ':', ';', '。', '、').ToLowerInvariant();
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        return result;
    }
}
