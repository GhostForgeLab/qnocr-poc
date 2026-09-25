using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

internal static class BindingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV19", Guid.NewGuid().ToString("N")));
        var mapping = Path.GetFullPath("Validation/QnbotCompatibility/identity_mapping.json");
        var registry = new SessionRegistry(root, "binding-check", mapping);
        var t0 = new DateTimeOffset(2026, 9, 25, 22, 0, 0, TimeSpan.FromHours(8));
        var productA = "1051087533910";
        var productB = "1111111111111";
        var productC = "2222222222222";
        var a = registry.Resolve("buyer-A", productA, t0, "capture-A-1");
        var b = registry.Resolve("buyer-B", productB, t0.AddSeconds(1), "capture-B-1");
        check(a.SessionId != b.SessionId && a.BuyerId != b.BuyerId
            && a.BuyerDisplayName == "buyer-A" && b.BuyerDisplayName == "buyer-B",
            "two OCR buyers bind to distinct sessions and isolated buyer IDs");
        check(a.CreatedAt == t0 && a.LastActivityAt == t0 && b.CreatedAt == t0.AddSeconds(1)
            && a.ProductReferences?.Single().ProductId == productA
            && b.ProductReferences?.Single().ProductId == productB,
            "each session starts with its own capture-timed product reference");
        var aUnknown = registry.Resolve("buyer-A", "", t0.AddSeconds(2), "capture-A-2");
        check(aUnknown.ProductId == "" && aUnknown.ProductReferences?.Single().ProductId == productA,
            "missing visible product stays empty while historical reference remains separate");
        var aChanged = registry.Resolve("buyer-A", productC, t0.AddSeconds(3), "capture-A-3");
        check(aChanged.CreatedAt == t0 && aChanged.LastActivityAt == t0.AddSeconds(3)
            && aChanged.ProductReferences?.Select(reference => reference.ProductId).Order().SequenceEqual(new[] { productA, productC }.Order()) == true
            && !aChanged.ProductReferences.Any(reference => reference.ProductId == productB),
            "product change updates only buyer A timeline; buyer B reference never leaks");
        var resumed = new SessionRegistry(root, "binding-check", mapping)
            .Resolve("buyer-A", "", t0.AddSeconds(4), "capture-A-4");
        check(resumed.SessionId == a.SessionId && resumed.CreatedAt == t0 && resumed.LastActivityAt == t0.AddSeconds(4)
            && resumed.ProductReferences?.Count == 2 && resumed.ProductId == "",
            "session identity, creation time and reference history survive registry restart without stale current ID");
        var invalid = registry.Resolve("buyer-B", "not-a-product-id", t0.AddSeconds(5), "capture-B-2");
        check(invalid.ProductId == "" && invalid.ProductReferences?.Single().ProductId == productB,
            "invalid product identifier never becomes a current or historical reference");

        DispatchItem Item(MessageSession session, string text, string product, string id, DateTimeOffset captured) =>
            new(id, id, session, new("Qianniu.OCR", session.SellerId, session.BuyerId, product,
                [new("buyer", text, captured.DateTime)]) { ProductBinding = "SameCaptureUnverified" }, captured, false,
                CaptureId: "capture-" + id[..2]);
        var olderA = Item(a, "问题A", productA, new string('A', 64), t0);
        var olderB = Item(b, "问题B", productB, new string('B', 64), t0.AddSeconds(1));
        var currentA = Item(resumed, "新问题A", "", new string('C', 64), t0.AddSeconds(4));
        var context = SessionContextBuilder.Build(currentA, [olderA, olderB]);
        check(context.SessionId == a.SessionId && context.BuyerId == a.BuyerId
            && context.BuyerDisplayName == "buyer-A" && context.CreatedAt == t0
            && context.Messages.Select(message => message.Text).SequenceEqual(new[] { "问题A", "新问题A" }),
            "session context carries the right buyer and only that buyer's message timeline");
        check(context.ProductId == "" && context.ProductReferences?.Select(reference => reference.ProductId).Order()
            .SequenceEqual(new[] { productA, productC }.Order()) == true
            && context.MessageProductLinks?.Select(link => link.ProductId).SequenceEqual(new[] { productA, "" }) == true,
            "context distinguishes historical product references from an unbound current message");
        var persisted = File.ReadAllText(Path.Combine(root, "sessions", a.SessionId, "session.json"));
        using var doc = JsonDocument.Parse(persisted);
        check(doc.RootElement.GetProperty("ProductReferences").GetArrayLength() == 2
            && !persisted.Contains("ProductName", StringComparison.Ordinal)
            && !persisted.Contains("Specifications", StringComparison.Ordinal),
            "persistent session saves product ID references only, without catalog or knowledge fields");

        var legacyId = MessageFingerprint.SessionId("seller-test", "buyer-test", "legacy-buyer");
        var legacyFolder = Path.Combine(root, "sessions", legacyId);
        Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(Path.Combine(legacyFolder, "session.json"), JsonSerializer.Serialize(new
        {
            SessionId = legacyId, SellerId = "seller-test", BuyerId = "buyer-test:" + legacyId[..16],
            ObservedBuyer = "legacy-buyer", IdentityBinding = "IsolatedTestIdentity", ProductId = "", IdentityPath = (string?)null
        }));
        var migrated = new SessionRegistry(root, "binding-check", mapping)
            .Resolve("legacy-buyer", productA, t0.AddSeconds(6), "capture-legacy");
        check(migrated.SessionId == legacyId && migrated.CreatedAt == t0.AddSeconds(6)
            && migrated.ProductReferences?.Single().ProductId == productA,
            "pre-V1.9 session JSON gains identity timestamps and references without changing its session ID");
    }
}
