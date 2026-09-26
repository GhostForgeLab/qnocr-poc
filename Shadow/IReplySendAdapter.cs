using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public interface IReplySendAdapter
{
    string Mode { get; }
    Task<SendAdapterResult> SendAsync(ConfirmedSendRequest request, CancellationToken token);
}

/// <summary>Test-only endpoint: records an explicit send request locally and never contacts Qianniu.</summary>
public sealed class IsolatedRecordingSendAdapter(string directory) : IReplySendAdapter
{
    public string Mode => "Isolated";

    public Task<SendAdapterResult> SendAsync(ConfirmedSendRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AtomicJson.Write(Path.Combine(directory, "isolated-send-request.json"), request);
        return Task.FromResult(new SendAdapterResult("IsolatedRecorded", true, false,
            "Request recorded locally; no qnbot or Qianniu send was invoked.", DateTimeOffset.Now));
    }
}

/// <summary>Port for a future verified qnbot runtime host; no production port is configured by this POC.</summary>
public interface IQnbotSendPort
{
    Task<bool> SendTextWithRetryAsync(string sellerNick, string buyerNick, string text, CancellationToken token);
}

public sealed record VerifiedQnbotSendTarget(string SessionId, string BuyerId, string SellerNick,
    string BuyerNick, bool PlatformIdentityVerified);

public sealed class QnbotSendAdapter(IQnbotSendPort port, VerifiedQnbotSendTarget target, BuyerIdentity? identity = null) : IReplySendAdapter
{
    public string Mode => "QnbotVerified";

    public async Task<SendAdapterResult> SendAsync(ConfirmedSendRequest request, CancellationToken token)
    {
        var identityDecision = BuyerIdentitySafety.CanSendToBuyer(identity, request.SessionId, request.BuyerId, DateTimeOffset.Now);
        if (!identityDecision.Allowed)
            return new(identityDecision.Code, false, false, identityDecision.Detail, DateTimeOffset.Now);
        if (!target.PlatformIdentityVerified || target.SessionId != request.SessionId || target.BuyerId != request.BuyerId
            || string.IsNullOrWhiteSpace(target.SellerNick) || string.IsNullOrWhiteSpace(target.BuyerNick)
            || request.BuyerId.StartsWith("buyer-test:", StringComparison.Ordinal))
            return new("IdentityUnverified", false, false,
                "Verified platform buyer and seller identity is required; no send attempted.", DateTimeOffset.Now);
        var sent = await port.SendTextWithRetryAsync(target.SellerNick, target.BuyerNick, request.Reply, token);
        return new(sent ? "QnbotAccepted" : "QnbotRejected", sent, sent,
            sent ? null : "qnbot send capability returned false.", DateTimeOffset.Now);
    }
}
