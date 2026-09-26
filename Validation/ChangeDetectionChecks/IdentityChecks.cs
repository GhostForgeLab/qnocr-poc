using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class IdentityChecks
{
    private sealed class FixtureSource(TrustedBuyerIdentityProof? proof) : ITrustedBuyerIdentitySource
    {
        public int Calls { get; private set; }
        public Task<TrustedBuyerIdentityProof?> VerifyAsync(MessageSession session, CancellationToken token)
        {
            Calls++; return Task.FromResult(proof);
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV111A", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var t0 = new DateTimeOffset(2026, 9, 26, 15, 0, 0, TimeSpan.FromHours(8));
        var testMap = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var testRegistry = new SessionRegistry(Path.Combine(root, "test-state"), "test-run", testMap);
        var testSession = testRegistry.Resolve("test-buyer", "", t0, "test-capture");
        check(testSession.Identity?.Status == BuyerIdentityStatuses.Mapped && testSession.Identity.IsTest
            && testSession.BuyerId.StartsWith("buyer-test:", StringComparison.Ordinal)
            && !BuyerIdentitySafety.CanSendToBuyer(testSession.Identity, testSession.SessionId, testSession.BuyerId, t0).Allowed,
            "buyer-test mapping stays explicitly test/unverified and cannot pass send precheck");
        var envelope = new MessageEnvelope("Qianniu.OCR", testSession.SellerId, testSession.BuyerId, "",
            [new EnvelopeMessage("buyer", "合成身份检查", t0.DateTime)]);
        var item = new DispatchItem(new string('A', 64), new string('B', 64), testSession, envelope,
            t0, false, CaptureId: "identity-check:frame_000001");
        var context = SessionContextBuilder.Build(item, Array.Empty<DispatchItem>());
        var wireFolder = Path.Combine(root, "wire");
        Directory.CreateDirectory(wireFolder);
        File.WriteAllText(Path.Combine(wireFolder, "context.json"), JsonSerializer.Serialize(context));
        File.WriteAllText(Path.Combine(wireFolder, "envelope.json"), JsonSerializer.Serialize(envelope));
        var traceDir = Path.Combine(root, "trace");
        var trace = new AgentPipelineTrace(traceDir);
        trace.MessageCaptured(item);
        var buyerBound = File.ReadAllLines(Path.Combine(traceDir, "agent-pipeline.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(row => row.GetProperty("Event").GetString() == "BUYER_BOUND");
        check(context.Identity?.Status == BuyerIdentityStatuses.Mapped && context.Identity.IsTest
            && buyerBound.GetProperty("Detail").GetString()!.Contains("IdentityStatus=Mapped", StringComparison.Ordinal),
            "Session Context and binding audit expose test identity status without changing MessageEnvelope");
        var testProof = new TrustedBuyerIdentityProof(testSession.SessionId, testSession.SellerId,
            testSession.BuyerId, "test-buyer", "conversation-test", "evidence-test", t0,
            BuyerIdentitySafety.TrustedSource, 1.0);
        var testSource = new FixtureSource(testProof);
        var testOutcome = await new SessionIdentityBindingService(Path.Combine(root, "test-state"), () => t0)
            .VerifyAsync(testSession.SessionId, testSource);
        check(!testOutcome.Decision.Allowed && testOutcome.Decision.Code == "TEST_IDENTITY" && testSource.Calls == 0
            && testOutcome.Identity?.Status != BuyerIdentityStatuses.Verified,
            "even a claimed trusted proof cannot upgrade buyer-test to Verified");

        var observed = new SessionRegistry(Path.Combine(root, "ocr-state"), "ocr-run", null)
            .Resolve("ocr-only-buyer", "", t0, "ocr-capture");
        check(observed.Identity?.Status == BuyerIdentityStatuses.Observed && observed.Identity.BuyerId == ""
            && observed.Identity.Source == "ChatHeaderOCR"
            && !BuyerIdentitySafety.CanSendToBuyer(observed.Identity, observed.SessionId, "", t0).Allowed,
            "OCR display label alone remains Observed and not a platform buyer identity");

        var mappingPath = Path.Combine(root, "verified-fixture-mapping.json");
        File.WriteAllText(mappingPath, JsonSerializer.Serialize(new
        {
            SellerId = "seller-platform-fixture", BuyerId = "buyer-platform-fixture",
            SellerNick = "seller-fixture", BuyerNick = "buyer-fixture", IsTestMapping = false
        }));
        var state = Path.Combine(root, "mapped-state");
        var registry = new SessionRegistry(state, "verified-fixture-run", mappingPath);
        var mapped = registry.Resolve("buyer-fixture", "", t0, "mapped-capture");
        check(mapped.Identity?.Status == BuyerIdentityStatuses.Mapped && !mapped.Identity.IsTest
            && !BuyerIdentitySafety.CanSendToBuyer(mapped.Identity, mapped.SessionId, mapped.BuyerId, t0).Allowed,
            "operator mapping records Mapped but cannot authorize a real send by itself");
        var mismatch = new TrustedBuyerIdentityProof(mapped.SessionId, mapped.SellerId, mapped.BuyerId,
            "different-buyer", "conversation-fixture", "evidence-fixture", t0,
            BuyerIdentitySafety.TrustedSource, 1.0);
        var service = new SessionIdentityBindingService(state, () => t0);
        var rejected = await service.VerifyAsync(mapped.SessionId, new FixtureSource(mismatch));
        check(rejected.Decision.Code == "PROOF_MISMATCH" && !rejected.Decision.Allowed
            && JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(Path.Combine(state, "sessions", mapped.SessionId, "session.json")))?.Identity?.Status
                == BuyerIdentityStatuses.Mapped,
            "proof with a different platform display identity cannot bind this OCR session");
        var unavailable = await service.VerifyAsync(mapped.SessionId, new UnavailableBuyerIdentitySource());
        check(unavailable.Decision.Code == "TRUSTED_SOURCE_UNAVAILABLE" && !unavailable.Decision.Allowed,
            "missing qnbot runtime proof fails closed without manufacturing Verified");
        var proof = mismatch with { BuyerDisplayName = mapped.BuyerDisplayName! };
        var verified = await service.VerifyAsync(mapped.SessionId, new FixtureSource(proof));
        check(verified.Decision.Allowed && verified.Identity?.Status == BuyerIdentityStatuses.Verified
            && verified.Identity.Source == BuyerIdentitySafety.TrustedSource
            && verified.Identity.SessionId == mapped.SessionId && verified.Identity.BuyerId == mapped.BuyerId,
            "synthetic trusted-source fixture can produce a session-bound Verified identity");
        check(BuyerIdentitySafety.CanSendToBuyer(verified.Identity, mapped.SessionId, mapped.BuyerId, t0).Allowed
            && !BuyerIdentitySafety.CanSendToBuyer(verified.Identity, testSession.SessionId, mapped.BuyerId, t0).Allowed
            && !BuyerIdentitySafety.CanSendToBuyer(verified.Identity, mapped.SessionId, "other-buyer", t0).Allowed
            && !BuyerIdentitySafety.CanSendToBuyer(verified.Identity, mapped.SessionId, mapped.BuyerId, t0.AddMinutes(6)).Allowed,
            "send precheck requires exact session/buyer and a fresh verified proof");
        var persisted = JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(Path.Combine(state, "sessions", mapped.SessionId, "session.json")));
        check(persisted?.Identity?.Status == BuyerIdentityStatuses.Verified
            && File.Exists(Path.Combine(state, "sessions", mapped.SessionId, "buyer-identity.json")),
            "verified identity is persisted beside the exact existing session");
        var invalidated = await service.VerifyAsync(mapped.SessionId, new UnavailableBuyerIdentitySource());
        check(invalidated.Decision.Code == "TRUSTED_SOURCE_UNAVAILABLE"
            && invalidated.Identity?.Status == BuyerIdentityStatuses.Mapped
            && !BuyerIdentitySafety.CanSendToBuyer(invalidated.Identity, mapped.SessionId, mapped.BuyerId, t0).Allowed,
            "failed fresh verification invalidates a previously Verified session");
        var reobserved = new SessionRegistry(state, "next-ocr-run", mappingPath)
            .Resolve("buyer-fixture", "", t0.AddMinutes(1), "next-capture");
        check(reobserved.SessionId == mapped.SessionId && reobserved.Identity?.Status == BuyerIdentityStatuses.Mapped
            && !BuyerIdentitySafety.CanSendToBuyer(reobserved.Identity, mapped.SessionId, mapped.BuyerId, t0.AddMinutes(1)).Allowed,
            "new OCR observation never carries forward Verified without fresh platform proof");
    }
}
