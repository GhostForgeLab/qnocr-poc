using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QNAgent;

internal sealed record ShopIdentity(string Id, string BindingKey, string DisplayName);
internal sealed record LearningCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ShopId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SourceKey { get; init; } = "";
    public string SampleHash { get; init; } = "";
    public string ProductHash { get; init; } = "";
    public string Question { get; init; } = "";
    public string AiReply { get; init; } = "";
    public string HumanReply { get; init; } = "";
    public string Context { get; init; } = "";
    public string Kind { get; init; } = "Unclear";
    public string Status { get; init; } = "PendingAnalysis";
    public string Proposal { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Evidence { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
internal sealed record LearnedRule(string Id, string ShopId, string ProductId, string Kind, string Question,
    string Content, string ProductHash, string CandidateId, DateTimeOffset ConfirmedAt, bool Active = true);
internal sealed record BusinessState
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public ShopIdentity[] Shops { get; init; } = Array.Empty<ShopIdentity>();
    public CatalogProduct[] Products { get; init; } = Array.Empty<CatalogProduct>();
    public ShopKnowledge[] Policies { get; init; } = Array.Empty<ShopKnowledge>();
    public KnowledgeAdviceState[] Advice { get; init; } = Array.Empty<KnowledgeAdviceState>();
    public LearningCandidate[] Candidates { get; init; } = Array.Empty<LearningCandidate>();
    public LearnedRule[] Rules { get; init; } = Array.Empty<LearnedRule>();
    public bool LearningEnabled { get; init; } = true;
}
internal sealed class BusinessRepository(ClientStore store)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private object Gate => Gates.GetOrAdd(store.Root, _ => new object());
    internal const int MaxBytes = 32 * 1024 * 1024;
    internal BusinessState Read()
    {
        lock (Gate)
        {
            var path = Path.Combine(store.Root, "business.json");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("业务资料超过大小限制。");
            var existing = store.ReadValue<BusinessState>("business.json");
            if (existing is not null) { Validate(existing); return existing; }
            if (File.Exists(path)) throw new InvalidDataException("业务资料为空或损坏，请使用业务备份恢复。");
            // Non-destructive migration. Original files stay in place; business.json becomes authoritative.
            var products = store.ReadValue<CatalogProduct[]>("products.json") ?? Array.Empty<CatalogProduct>();
            var policies = store.ReadValue<ShopKnowledge[]>("shop-knowledge.json") ?? Array.Empty<ShopKnowledge>();
            var keys = products.Select(p => p.SellerId).Concat(policies.Select(p => p.SellerId)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
            var shops = keys.Select(key => new ShopIdentity(Guid.NewGuid().ToString("N"), key, key)).ToArray();
            products = products.Select(p => p with { ShopId = shops.Single(s => s.BindingKey == p.SellerId).Id }).ToArray();
            var folder = Path.Combine(store.Root, "knowledge-advice");
            var advice = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Take(2000).Select(file =>
                JsonSerializer.Deserialize<KnowledgeAdviceState>(File.ReadAllText(file), ClientStore.Json)!).Where(a => a is not null && products.Any(p => p.SellerId == a.SellerId && p.ProductId == a.ProductId)).ToArray() : Array.Empty<KnowledgeAdviceState>();
            var state = new BusinessState { Shops = shops, Products = products, Policies = policies, Advice = advice };
            Write(state); return state;
        }
    }
    internal BusinessState Update(Func<BusinessState, BusinessState> update, long? expectedRevision = null)
    {
        lock (Gate)
        {
            var previous = Read();
            if (expectedRevision is not null && previous.Revision != expectedRevision) throw new InvalidOperationException("业务资料已变化，请重新预览后恢复。");
            var next = update(previous) with { Revision = previous.Revision + 1 }; Write(next); return next;
        }
    }
    private void Write(BusinessState state)
    {
        Validate(state);
        if (JsonSerializer.SerializeToUtf8Bytes(state, ClientStore.Json).Length > MaxBytes) throw new InvalidDataException("业务资料超过 32 MB，请先整理旧学习建议。");
        store.Write("business.json", state);
    }
    internal ShopIdentity Resolve(string input, bool create = true)
    {
        input = input.Trim();
        if (input.Length is 0 or > 128 || input.Any(char.IsControl)) throw new InvalidDataException("请填写有效的固定店铺标识。");
        var state = Read();
        var parsedId = Guid.TryParse(input, out var guid) ? guid.ToString("N") : null;
        var direct = state.Shops.SingleOrDefault(s => s.Id == parsedId || s.BindingKey == input);
        if (direct is not null) return direct;
        var named = state.Shops.Where(s => s.DisplayName == input).ToArray();
        if (named.Length == 1) return named[0];
        if (named.Length > 1 || !create) throw new InvalidDataException("店铺名称不唯一或不存在，请使用固定 ID。");
        var shop = new ShopIdentity(parsedId ?? Guid.NewGuid().ToString("N"), input, input);
        return Update(s => s with { Shops = s.Shops.Any(x => x.BindingKey == input) ? s.Shops : s.Shops.Append(shop).ToArray() }).Shops.Single(s => s.BindingKey == input);
    }
    internal ShopIdentity CreateShop(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128) throw new InvalidDataException("请填写店铺名称，最多 128 字。");
        var id = Guid.NewGuid().ToString("N"); var shop = new ShopIdentity(id, id, name.Trim());
        Update(s => s with { Shops = s.Shops.Append(shop).ToArray() }); return shop;
    }
    internal void Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128) throw new InvalidDataException("请填写有效的店铺名称。");
        Update(s => s with { Shops = s.Shops.Select(shop => shop.Id == id ? shop with { DisplayName = name.Trim() } : shop).ToArray() });
    }
    internal static void Validate(BusinessState s)
    {
        if (s.SchemaVersion != 1 || s.Shops is null || s.Products is null || s.Policies is null || s.Advice is null || s.Candidates is null || s.Rules is null ||
            s.Shops.Length > 200 || s.Products.Length > 2000 || s.Candidates.Length > 1000 || s.Rules.Length > 500 || s.Advice.Length > 2000 || s.Policies.Length > 200)
            throw new InvalidDataException("业务资料版本或容量不受支持。");
        if (s.Shops.Any(p => p is null || !Guid.TryParseExact(p.Id, "N", out _) || string.IsNullOrWhiteSpace(p.BindingKey) || p.BindingKey.Length > 128 ||
            string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 128 || p.BindingKey.Any(char.IsControl) || p.DisplayName.Any(char.IsControl)) || s.Shops.Select(p => p.Id).Distinct().Count() != s.Shops.Length ||
            s.Shops.Select(p => p.BindingKey).Distinct(StringComparer.Ordinal).Count() != s.Shops.Length) throw new InvalidDataException("店铺标识重复或无效。");
        if (s.Shops.Any(p => Guid.TryParse(p.BindingKey, out var alias) && s.Shops.Any(other => other.Id == alias.ToString("N") && other.Id != p.Id)))
            throw new InvalidDataException("店铺旧标识与另一店铺固定 ID 冲突，请先检查备份中的店铺身份。");
        foreach (var p in s.Products)
        {
            ClientStore.Validate(p);
            if (!s.Shops.Any(shop => shop.Id == p.ShopId && shop.BindingKey == p.SellerId)) throw new InvalidDataException("商品与固定店铺 ID 不匹配。");
        }
        if (s.Products.GroupBy(p => (p.ShopId, p.ProductId)).Any(g => g.Count() > 1)) throw new InvalidDataException("业务包中有重复商品。");
        foreach (var p in s.Policies) { CatalogValidation.ValidateShop(p); if (!s.Shops.Any(shop => shop.BindingKey == p.SellerId)) throw new InvalidDataException("优惠所属店铺不存在。"); }
        if (s.Policies.GroupBy(p => p.SellerId).Any(g => g.Count() > 1)) throw new InvalidDataException("店铺优惠重复。");
        foreach (var a in s.Advice)
            if (a is null || a.Suggestions is null || a.Ignored is null || a.Suggestions.Length > 8 || a.Ignored.Length > 1000 ||
                !s.Products.Any(p => p.SellerId == a.SellerId && p.ProductId == a.ProductId) ||
                a.ContentHash is null || a.ContentHash.Length > 128 || a.Model is null || a.Model.Length > 200 ||
                a.Suggestions.Any(x => x is null || new[] { x.Topic, x.Category, x.Priority, x.Status, x.Target, x.Question, x.Reason, x.SuggestedAction }.Any(v => v is null) ||
                    !KnowledgeAdviceService.Targets.ContainsKey(x.Target) || x.Topic.Length > 100 || x.Question.Length > 180 || x.Reason.Length > 240 || x.SuggestedAction.Length > 240) ||
                a.Ignored.Any(x => x is null || x.Id is null || x.Question is null || x.Id.Length > 100 || x.Question.Length > 180))
                throw new InvalidDataException("资料建议与商品不匹配或格式无效。");
        foreach (var c in s.Candidates)
            if (c is null || new[] { c.Question, c.HumanReply, c.AiReply, c.Context, c.Proposal, c.Reason, c.Evidence, c.SourceKey, c.SampleHash, c.ProductHash }.Any(v => v is null) ||
                c.SourceKey.Length > 128 || c.SampleHash.Length > 128 || c.ProductHash.Length > 128 || !Guid.TryParseExact(c.Id, "N", out _) || !s.Products.Any(p => p.ShopId == c.ShopId && p.ProductId == c.ProductId) ||
                c.Question.Length > 1000 || c.HumanReply.Length > 4000 || c.AiReply.Length > 4000 || c.Context.Length > 3000 || c.Proposal.Length > 2000 || c.Reason.Length > 1000 || c.Evidence.Length > 4000 ||
                c.Status is not ("PendingAnalysis" or "Pending" or "AnalysisFailed" or "Approved" or "Ignored") ||
                c.Kind is not ("Knowledge" or "Style" or "Intent" or "CaseSpecific" or "Unclear" or "NoChange")) throw new InvalidDataException("学习建议格式无效。");
        foreach (var r in s.Rules)
            if (r is null || r.Question is null || r.ProductHash is null || r.ProductHash.Length > 128 || !Guid.TryParseExact(r.CandidateId, "N", out _) ||
                !Guid.TryParseExact(r.Id, "N", out _) || !s.Products.Any(p => p.ShopId == r.ShopId && p.ProductId == r.ProductId) ||
                r.Kind is not ("Knowledge" or "Style" or "Intent") || string.IsNullOrWhiteSpace(r.Content) || r.Content.Length > 2000 || r.Question.Length > 1000)
                throw new InvalidDataException("已确认经验格式无效。");
        if (s.Candidates.Select(c => c.Id).Distinct().Count() != s.Candidates.Length || s.Rules.Select(r => r.Id).Distinct().Count() != s.Rules.Length ||
            s.Candidates.GroupBy(c => c.SourceKey).Any(g => g.Count() > 1) || s.Advice.GroupBy(a => (a.SellerId, a.ProductId)).Any(g => g.Count() > 1))
            throw new InvalidDataException("学习记录标识重复。");
    }
}
