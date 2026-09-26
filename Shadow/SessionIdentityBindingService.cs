using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed record TrustedBuyerIdentityProof(string SessionId, string SellerId, string BuyerId,
    string BuyerDisplayName, string PlatformConversationId, string EvidenceId,
    DateTimeOffset CapturedAt, string Source, double Confidence);

/// <summary>Only an independently verified qnbot runtime source may provide this proof.</summary>
public interface ITrustedBuyerIdentitySource
{
    Task<TrustedBuyerIdentityProof?> VerifyAsync(MessageSession session, CancellationToken token);
}

public sealed class UnavailableBuyerIdentitySource : ITrustedBuyerIdentitySource
{
    public Task<TrustedBuyerIdentityProof?> VerifyAsync(MessageSession session, CancellationToken token) =>
        Task.FromResult<TrustedBuyerIdentityProof?>(null);
}

public sealed record BuyerIdentityVerificationOutcome(BuyerIdentity? Identity, BuyerSendDecision Decision);

public static class BuyerIdentitySafety
{
    public const string TrustedSource = "QnbotRuntimeIdentity";
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    public static BuyerSendDecision CanSendToBuyer(BuyerIdentity? identity,
        string expectedSessionId, string expectedBuyerId, DateTimeOffset now)
    {
        if (identity is null || identity.Status != BuyerIdentityStatuses.Verified)
            return new(false, "IDENTITY_UNVERIFIED", "Verified platform buyer identity is required.");
        if (identity.IsTest || identity.BuyerId.StartsWith("buyer-test", StringComparison.OrdinalIgnoreCase))
            return new(false, "TEST_IDENTITY", "Isolated test buyer identity cannot be a delivery target.");
        if (!string.Equals(identity.SessionId, expectedSessionId, StringComparison.Ordinal))
            return new(false, "SESSION_MISMATCH", "Verified identity belongs to another session.");
        if (!string.Equals(identity.BuyerId, expectedBuyerId, StringComparison.Ordinal))
            return new(false, "BUYER_MISMATCH", "Verified platform buyer ID differs from the target.");
        if (identity.Source != TrustedSource || string.IsNullOrWhiteSpace(identity.PlatformConversationId)
            || string.IsNullOrWhiteSpace(identity.EvidenceId) || identity.Confidence != 1.0)
            return new(false, "EVIDENCE_UNVERIFIED", "Independent platform conversation evidence is incomplete.");
        if (identity.VerifiedAt is not { } verifiedAt || verifiedAt > now.AddSeconds(30)
            || now - verifiedAt > Freshness)
            return new(false, "IDENTITY_EXPIRED", "Fresh platform verification is required before sending.");
        return new(true, "VERIFIED", "Identity is a fresh exact platform/session match.");
    }
}

/// <summary>There is no CLI command that can turn an OCR label or test map into Verified.</summary>
public sealed class SessionIdentityBindingService(string stateRoot, Func<DateTimeOffset>? clock = null)
{
    private readonly string _root = Path.GetFullPath(stateRoot);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task<BuyerIdentityVerificationOutcome> VerifyAsync(
        string sessionId, ITrustedBuyerIdentitySource source, CancellationToken token = default)
    {
        if (sessionId.Length != 64 || !sessionId.All(Uri.IsHexDigit))
            throw new ArgumentException("A full session ID is required.", nameof(sessionId));
        var folder = Path.Combine(_root, "sessions", sessionId);
        var path = Path.Combine(folder, "session.json");
        if (!File.Exists(path)) return Rejected(null, sessionId, "SESSION_NOT_FOUND");
        var session = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Session state is unreadable; identity not verified.");
        if (session.SessionId != sessionId) throw new InvalidDataException("Session identity mismatch.");
        if (session.Identity?.IsTest == true || session.BuyerId.StartsWith("buyer-test", StringComparison.OrdinalIgnoreCase))
            return Rejected(session, sessionId, "TEST_IDENTITY");
        var proof = await source.VerifyAsync(session, token);
        if (proof is null) return Rejected(session, sessionId, "TRUSTED_SOURCE_UNAVAILABLE");
        var now = _clock();
        if (proof.Source != BuyerIdentitySafety.TrustedSource || proof.SessionId != session.SessionId
            || proof.SellerId != session.SellerId || proof.BuyerId != session.BuyerId
            || proof.BuyerDisplayName != session.BuyerDisplayName
            || string.IsNullOrWhiteSpace(proof.BuyerId) || string.IsNullOrWhiteSpace(proof.PlatformConversationId)
            || string.IsNullOrWhiteSpace(proof.EvidenceId) || proof.Confidence != 1.0
            || proof.CapturedAt > now.AddSeconds(30) || now - proof.CapturedAt > BuyerIdentitySafety.Freshness)
            return Rejected(session, sessionId, "PROOF_MISMATCH");
        var identity = new BuyerIdentity(proof.BuyerId, proof.Source, proof.Confidence,
            proof.CapturedAt, session.SessionId, BuyerIdentityStatuses.Verified,
            session.BuyerDisplayName, proof.PlatformConversationId, proof.EvidenceId, false, now);
        RunLogWriter.Append(_root, "identity-audit.jsonl", new
        {
            At = now, Event = "BUYER_IDENTITY_VERIFIED", session.SessionId, BuyerId = identity.BuyerId,
            identity.Source, identity.EvidenceId, identity.PlatformConversationId, identity.Confidence
        });
        AtomicJson.Write(path, session with { Identity = identity });
        AtomicJson.Write(Path.Combine(folder, "buyer-identity.json"), identity);
        return new(identity, new(true, "VERIFIED", "Independent runtime proof bound to the existing session."));
    }

    private BuyerIdentityVerificationOutcome Rejected(MessageSession? session, string sessionId, string code)
    {
        var existing = session?.Identity;
        if (session is not null && existing?.Status == BuyerIdentityStatuses.Verified)
        {
            existing = existing with { Status = BuyerIdentityStatuses.Mapped, Source = "VerificationInvalidated",
                Confidence = null, PlatformConversationId = null, EvidenceId = null, VerifiedAt = null };
            var folder = Path.Combine(_root, "sessions", sessionId);
            AtomicJson.Write(Path.Combine(folder, "session.json"), session with { Identity = existing });
            AtomicJson.Write(Path.Combine(folder, "buyer-identity.json"), existing);
        }
        RunLogWriter.Append(_root, "identity-audit.jsonl", new
        {
            At = _clock(), Event = "BUYER_IDENTITY_REJECTED", SessionId = sessionId, Code = code
        });
        return new(existing, new(false, code, "No verified platform identity was persisted."));
    }
}
