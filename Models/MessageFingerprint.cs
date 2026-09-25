using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QnocrPoc.Models;

public static class MessageFingerprint
{
    // Keep original text in the envelope. Only comparison uses compatibility-width normalization.
    public static string NormalizeText(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s+", " ").Trim();
    public static string Create(BuyerMessage message) => Hash(message.Role, NormalizeText(message.Text),
        message.Timestamp.ToString("O", CultureInfo.InvariantCulture), message.ProductId);

    // A visible product-panel change must not turn the same already-seen chat message into an arrival.
    public static string ConversationMessageKey(BuyerMessage message) => Hash(message.Role, NormalizeText(message.Text),
        message.Timestamp.ToString("O", CultureInfo.InvariantCulture));
    public static string StableId(string sessionId, BuyerMessage message) => Hash("qnocr-v14", sessionId, ConversationMessageKey(message));
    public static string SessionId(string seller, string buyer, string observedBuyer) => Hash("session-v14", seller, buyer, observedBuyer);

    private static string Hash(params string[] fields)
    {
        var value = new StringBuilder();
        foreach (var field in fields) value.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
}
