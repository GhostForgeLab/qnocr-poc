using System.Text.RegularExpressions;

namespace QNAgent;

internal static class ReplyTextPolicy
{
    internal const string HumanReply = "这个问题目前还缺少可靠的商品资料，我先核实清楚，再给您准确答复。";
    internal const string ClarifyReply = "方便补充一下您咨询的具体款式和使用需求吗？确认后我再帮您核实。";
    internal static string Normalize(string text)
    {
        text = Regex.Replace(text, @"(?<=\d)\s*[*＊]\s*(?=\d)", "×");
        text = Regex.Replace(text, @"[*＊`]+|__", "");
        text = Regex.Replace(text, @"(?m)^\s*#{1,6}\s*", "");
        return text.Trim();
    }
    internal static bool Allowed(string text) => !Regex.IsMatch(text, "烟(?!盒)");
}
