using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed class SessionRegistry(string stateRoot, string runId, string? mappingPath)
{
    private readonly Dictionary<string, NewMessageTracker> _trackers = new(StringComparer.Ordinal);
    private string? _boundRealBuyer;
    private static readonly Regex DateText = new(@"\d{4}-\d{1,2}-\d{1,2}\s*\d{1,2}:\d{2}:\d{2}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string? ReadBuyer(BuyerMessageBatch batch, OcrResult ocr)
    {
        var labels = batch.Evidence.Select(e => string.Join(" ", e.HeaderLineNumbers.Select(n => ocr.Lines.Single(l => l.Number == n).Text)))
            .Select(text => DateText.Replace(MessageFingerprint.NormalizeText(text), "").Trim(' ', ':', '-', '|'))
            .Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return labels.Length == 1 ? labels[0] : null;
    }

    public MessageSession Resolve(string observedBuyer, string productId)
    {
        var map = mappingPath is null ? null : JsonNode.Parse(File.ReadAllText(mappingPath))!.AsObject();
        var seller = map?["SellerId"]?.GetValue<string>() ?? "";
        var buyer = map?["BuyerId"]?.GetValue<string>() ?? "";
        var test = map?["IsTestMapping"]?.GetValue<bool>() == true;
        var sid = MessageFingerprint.SessionId(seller == "" ? runId : seller, buyer, observedBuyer);
        var folder = Path.Combine(stateRoot, "sessions", sid);
        Directory.CreateDirectory(folder);
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
        var result = new MessageSession(sid, seller, buyer, observedBuyer,
            test ? "IsolatedTestIdentity; OcrLabelNotPlatformIdentity" : identity is null ? "Unverified; dispatch held" : "OperatorMapping; OcrLabelNotPlatformIdentity", productId, identity);
        AtomicJson.Write(Path.Combine(folder, "session.json"), result);
        return result;
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
