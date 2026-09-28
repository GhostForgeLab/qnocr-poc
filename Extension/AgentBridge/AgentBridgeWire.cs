using System.Text.Json;

namespace QnocrPoc.Extension.AgentBridge;

internal static class AgentBridgeWire
{
    internal const int MaxBytes = 16 * 1024;

    internal static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 100 ||
            !pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Pipe name must be 1-100 ASCII letters, digits, hyphens or underscores.", nameof(pipeName));
    }

    internal static async Task WriteAsync(Stream stream, AgentEventEnvelope value, CancellationToken token)
    {
        value.ValidateForTransport();
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxBytes) throw new InvalidDataException("Agent bridge frame too large.");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<AgentEventEnvelope> ReadAsync(Stream stream, CancellationToken token)
    {
        var sizeBuffer = new byte[4];
        await stream.ReadExactlyAsync(sizeBuffer, token);
        var length = BitConverter.ToInt32(sizeBuffer);
        if (length is < 1 or > MaxBytes) throw new InvalidDataException("Invalid Agent bridge frame length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        var value = JsonSerializer.Deserialize<AgentEventEnvelope>(payload)
            ?? throw new InvalidDataException("Empty Agent bridge event.");
        value.ValidateForTransport();
        return value;
    }
}
