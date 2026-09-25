using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QnocrPoc.Models;

public static class MessageFingerprint
{
    // Length prefixes prevent collisions from delimiters inside OCR text. No lossy text normalization.
    public static string Create(BuyerMessage message) => Hash(message.Role, message.Text,
        message.Timestamp.ToString("O", CultureInfo.InvariantCulture), message.ProductId);

    // A visible product-panel change must not turn the same already-seen chat message into an arrival.
    public static string ConversationMessageKey(BuyerMessage message) => Hash(message.Role, message.Text,
        message.Timestamp.ToString("O", CultureInfo.InvariantCulture));

    private static string Hash(params string[] fields)
    {
        var value = new StringBuilder();
        foreach (var field in fields) value.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
}
