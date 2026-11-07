using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QnocrPoc.Models;
using QnocrPoc.Product;

namespace QnocrPoc.Shadow;

public sealed class SessionRegistry(string stateRoot, string runId, string? mappingPath)
{
    private readonly Dictionary<string, NewMessageTracker> _trackers = new(StringComparer.Ordinal);
    private string? _boundRealBuyer;
    private static readonly Regex DateText = new(@"\d{4}-\d{1,2}-\d{1,2}\s*\d{1,2}:\d{2}:\d{2}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string? ReadBuyer(BuyerMessageBatch batch, OcrResult ocr)
    {
        var labels = batch.Evidence.Where(e => batch.Messages[e.MessageIndex].Role == "buyer")
            .Select(e => string.Join(" ", e.HeaderLineNumbers.Select(n => ocr.Lines.Single(l => l.Number == n).Text)))
            .Select(text => DateText.Replace(MessageFingerprint.NormalizeText(text), "").Trim(' ', ':', '-', '|'))
            .Where(text => text.Length > 0).ToArray();
        var distinct = labels.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 1) return distinct[0];
        // A single trailing OCR glyph can appear beside an otherwise identical buyer header.
        // Require two exact anchors and only one such outlier; other mixed labels remain unbound.
        if (labels.Length < 3 || distinct.Length != 2) return null;
        var majority = labels.GroupBy(label => label, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).First();
        if (majority.Count() != labels.Length - 1 || majority.Key.Length < 6) return null;
        var outlier = distinct.Single(label => label != majority.Key);
        return outlier.Length == majority.Key.Length + 1
            && outlier.StartsWith(majority.Key, StringComparison.Ordinal)
            && char.IsDigit(outlier[outlier.Length - 1]) ? majority.Key : null;
    }

    public MessageSession Resolve(string observedBuyer, string productId, DateTimeOffset? observedAt = null, string? captureId = null)
    {
        var at = observedAt ?? DateTimeOffset.Now;
        productId = ProductIdExtractor.IsValidProductId(productId) ? productId : "";
        var map = mappingPath is null ? null : JsonNode.Parse(File.ReadAllText(mappingPath))!.AsObject();
        var seller = map?["SellerId"]?.GetValue<string>() ?? "";
        var buyer = map?["BuyerId"]?.GetValue<string>() ?? "";
        var test = map?["IsTestMapping"]?.GetValue<bool>() == true;
        var sid = MessageFingerprint.SessionId(seller == "" ? runId : seller, buyer, observedBuyer);
        var folder = Path.Combine(stateRoot, "sessions", sid);
        Directory.CreateDirectory(folder);
        var sessionFile = Path.Combine(folder, "session.json");
        var previous = File.Exists(sessionFile) ? JsonSerializer.Deserialize<MessageSession>(File.ReadAllText(sessionFile)) : null;
        if (File.Exists(sessionFile) && (previous is null || previous.SessionId != sid || previous.ObservedBuyer != observedBuyer))
            throw new InvalidDataException("Persisted buyer session is unreadable or does not match the observed identity.");
        string? identity = null;
        if (test)
        {
            // Isolated test identities are explicitly namespaced per observed buyer, never treated as real IDs.
            buyer += ":" + sid[..16];
            map!["BuyerId"] = buyer;
            map["BuyerNick"] = "offline_fixture_buyer_" + sid[..16];
            map["BuyerTargetId"] = "";
            identity = Path.Combine(folder, "identity.json");
            AtomicJson.Write(identity, map);
        }
        else if (map is not null)
        {
            _boundRealBuyer ??= observedBuyer;
            if (_boundRealBuyer == observedBuyer) { identity = Path.Combine(folder, "identity.json"); AtomicJson.Write(identity, map); }
        }
        var references = (previous?.ProductReferences ?? Array.Empty<ProductReference>())
            .Where(reference => ProductIdExtractor.IsValidProductId(reference.ProductId)).ToList();
        if (productId.Length > 0)
        {
            references.RemoveAll(reference => reference.ProductId == productId);
            references.Add(new ProductReference(productId, "Qianniu.ProductRegion.OCR", at,
                captureId ?? "", "VisibleInSameCapture;MessageAssociationUnverified"));
        }
        var buyerIdentity = new BuyerIdentity(buyer,
            test ? "IsolatedTestMapping" : identity is null ? "ChatHeaderOCR" : "OperatorMapping",
            null, at, sid, test || identity is not null ? BuyerIdentityStatuses.Mapped : BuyerIdentityStatuses.Observed,
            observedBuyer, IsTest: test);
        var result = new MessageSession(sid, seller, buyer, observedBuyer,
            test ? "IsolatedTestIdentity; OcrLabelNotPlatformIdentity" : identity is null ? "Unverified; dispatch held" : "OperatorMapping; OcrLabelNotPlatformIdentity",
            productId, identity, observedBuyer,
            previous?.CreatedAt is { } created && created != default ? created : at,
            at, references.OrderBy(reference => reference.CapturedAt).TakeLast(128).ToArray(), buyerIdentity);
        AtomicJson.Write(Path.Combine(folder, "buyer-identity.json"), buyerIdentity);
        AtomicJson.Write(sessionFile, result);
        return result;
    }
    public MessageSession BindCurrentProduct(MessageSession session, ProductContextResolution resolution,
        DateTimeOffset capturedAt, string captureId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Status == ProductContextBindingStatus.Bound && resolution.Current is null ||
            resolution.Status != ProductContextBindingStatus.Bound && resolution.Current is not null)
            throw new InvalidDataException("Product context status and current binding disagree.");
        var current = resolution.Current;
        if (current is not null && (current.SessionId != session.SessionId ||
            current.SellerId != session.SellerId || !ProductIdExtractor.IsValidProductId(current.ProductId) ||
            current.Source == ProductContextSource.OCR &&
                (current.CaptureId != captureId || current.CapturedAt != capturedAt)))
            throw new InvalidDataException("Product context differs from this session or capture.");
        var references = (session.ProductReferences ?? Array.Empty<ProductReference>()).ToList();
        if (current?.Source == ProductContextSource.Manual)
        {
            references.RemoveAll(reference => reference.ProductId == current.ProductId);
            references.Add(new ProductReference(current.ProductId, "ManualProductBinding", capturedAt,
                captureId, "CurrentOperatorBoundSession"));
        }
        var bound = session with
        {
            CurrentProductContext = current,
            ProductContextBindingStatus = resolution.Status,
            ProductReferences = references.OrderBy(reference => reference.CapturedAt).TakeLast(128).ToArray()
        };
        AtomicJson.Write(Path.Combine(stateRoot, "sessions", session.SessionId, "session.json"), bound);
        if (current is not null)
            RunLogWriter.Append(stateRoot, "product-context-audit.jsonl", new
            {
                Event = "PRODUCT_CONTEXT_BOUND", session.SessionId, current.ProductId,
                current.Source, current.Confidence, Timestamp = capturedAt, current.BoundBy, CaptureId = captureId
            });
        else if (resolution.Status == ProductContextBindingStatus.Conflict)
            RunLogWriter.Append(stateRoot, "product-context-audit.jsonl", new
            {
                Event = "PRODUCT_CONTEXT_CONFLICT", session.SessionId, Timestamp = capturedAt,
                Reason = resolution.Reason, CaptureId = captureId
            });
        return bound;
    }
    public NewMessageTracker Tracker(MessageSession session, DateTime now)
    {
        if (!_trackers.TryGetValue(session.SessionId, out var tracker))
        {
            if (_trackers.Count >= 256) _trackers.Remove(_trackers.Keys.First());
            _trackers[session.SessionId] = tracker = new(now);
        }
        return tracker;
    }
}

internal static class AtomicJson
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
