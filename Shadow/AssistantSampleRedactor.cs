using System.Text.RegularExpressions;

namespace QnocrPoc.Shadow;

/// <summary>Best-effort minimization for local feedback exports; source previews remain under the existing state policy.</summary>
public static class AssistantSampleRedactor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Secret = new(@"(?i)(?:sk|rk|key)-[A-Za-z0-9_-]{12,}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Bearer = new(@"(?i)Bearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Credential = new(@"(?i)(?:api[_-]?key|access[_-]?token|authorization|token|password|secret)\s*[:=]\s*['"" ]?[^\s,'""}]{6,}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Email = new(@"(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.Compiled, Timeout);
    private static readonly Regex Phone = new(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled, Timeout);

    public static string Redact(string text)
    {
        if (text.Length > 8000) text = text[..8000];
        try
        {
            text = Secret.Replace(text, "[REDACTED_SECRET]");
            text = Bearer.Replace(text, "[REDACTED_BEARER]");
            text = Credential.Replace(text, "[REDACTED_CREDENTIAL]");
            text = Email.Replace(text, "[REDACTED_EMAIL]");
            return Phone.Replace(text, "[REDACTED_PHONE]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTION_TIMEOUT]";
        }
    }
}
