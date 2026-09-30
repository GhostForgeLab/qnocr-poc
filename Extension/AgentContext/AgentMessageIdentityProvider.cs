using System.Security.Cryptography;
using System.Text;
using QnocrPoc.Models;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>Exact source ID, then timestamp/content fingerprint, then deterministic fallback.</summary>
public sealed class AgentMessageIdentityProvider : IAgentMessageIdentityProvider
{
    public string GetMessageId(SessionIdentity identity, EnvelopeMessage message,
        string? sourceMessageId = null, string? fallbackIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.BuyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Text);
        // Source IDs are opaque and Session-scoped, matching the existing explicit-ID overload.
        if (!string.IsNullOrWhiteSpace(sourceMessageId)) return sourceMessageId;

        // Length-prefix exact UTF-16 code units; separators, NULs and unpaired surrogates cannot alias.
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("AgentMessageIdentity/v1");
            WriteExact(writer, identity.Source);
            WriteExact(writer, identity.SellerId);
            WriteExact(writer, identity.BuyerId);
            WriteExact(writer, message.Role);
            if (message.Timestamp != default)
            {
                writer.Write("timestamp-content");
                writer.Write(message.Timestamp.Ticks);
                writer.Write((int)message.Timestamp.Kind);
                WriteExact(writer, message.Text);
            }
            else
            {
                writer.Write("fallback");
                writer.Write(!string.IsNullOrWhiteSpace(fallbackIdentity));
                WriteExact(writer, !string.IsNullOrWhiteSpace(fallbackIdentity) ? fallbackIdentity : message.Text);
            }
        }
        return "agent:v1:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteExact(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        foreach (var character in value) writer.Write((ushort)character);
    }
}
