using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QNAgent;

internal sealed record BusinessRestorePlan(BusinessState Incoming, long LocalRevision, int NewShops, int NewProducts, int ConflictingProducts, int RenamedBindings);
internal static class BusinessBackupService
{
    private sealed record Manifest(string Kind, int Version, DateTimeOffset CreatedAt, string SHA256);
    private static readonly JsonSerializerOptions Strict = new(ClientStore.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static string Export(ClientStore store, string path, BusinessState? snapshot = null)
    {
        var state = snapshot ?? store.Business.Read(); BusinessRepository.Validate(state); CheckSecrets(store, state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, ClientStore.Json);
        if (bytes.Length > BusinessRepository.MaxBytes) throw new InvalidDataException("备份资料过大。");
        var manifest = new Manifest("QNAgent.BusinessBackup", 1, DateTimeOffset.Now, Convert.ToHexString(SHA256.HashData(bytes)));
        var destination = Path.GetFullPath(path); var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var output = zip.CreateEntry("business.json").Open()) output.Write(bytes);
                using (var output = zip.CreateEntry("manifest.json").Open()) output.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, ClientStore.Json));
            }
            File.Move(temp, destination, true); return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static BusinessRestorePlan Preview(ClientStore store, string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != 2 || zip.Entries.Count(e => e.FullName == "manifest.json") != 1 || zip.Entries.Count(e => e.FullName == "business.json") != 1)
            throw new InvalidDataException("不是受支持的业务备份，或含有额外文件。不会解压或执行其中内容。");
        byte[] Read(string name, int limit)
        {
            var entry = zip.GetEntry(name)!; if (entry.Length > limit) throw new InvalidDataException("备份条目超过大小限制。");
            using var input = entry.Open(); using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = input.Read(buffer)) > 0) { if (output.Length + read > limit) throw new InvalidDataException("备份解压大小超过限制。"); output.Write(buffer, 0, read); }
            return output.ToArray();
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(Read("manifest.json", 8192), Strict) ?? throw new InvalidDataException("备份清单为空。");
        var payload = Read("business.json", BusinessRepository.MaxBytes);
        if (manifest.Kind != "QNAgent.BusinessBackup" || manifest.Version != 1 || manifest.SHA256 != Convert.ToHexString(SHA256.HashData(payload)))
            throw new InvalidDataException("备份版本不支持或文件校验失败。");
        var incoming = JsonSerializer.Deserialize<BusinessState>(payload, Strict) ?? throw new InvalidDataException("业务数据为空。");
        BusinessRepository.Validate(incoming); CheckSecrets(store, incoming);
        var local = store.Business.Read(); var rewritten = 0;
        var shops = incoming.Shops.Select(shop =>
        {
            var known = local.Shops.SingleOrDefault(s => s.Id == shop.Id);
            if (known is not null) return shop with { BindingKey = known.BindingKey };
            if (local.Shops.Any(s => s.BindingKey == shop.BindingKey || Guid.TryParse(shop.BindingKey, out var alias) && s.Id == alias.ToString("N")))
            { rewritten++; return shop with { BindingKey = shop.Id }; }
            return shop;
        }).ToArray();
        string Seller(string old) => shops.Single(s => s.Id == incoming.Shops.Single(o => o.BindingKey == old).Id).BindingKey;
        incoming = incoming with { Shops = shops, Products = incoming.Products.Select(p => p with { SellerId = Seller(p.SellerId) }).ToArray(),
            Policies = incoming.Policies.Select(p => p with { SellerId = Seller(p.SellerId) }).ToArray(),
            Advice = incoming.Advice.Select(a => a with { SellerId = Seller(a.SellerId) }).ToArray() };
        BusinessRepository.Validate(incoming);
        BusinessRepository.Validate(local with { Shops = local.Shops.Concat(shops.Where(s => local.Shops.All(l => l.Id != s.Id))).ToArray() });
        return new(incoming, local.Revision, shops.Count(s => local.Shops.All(l => l.Id != s.Id)),
            incoming.Products.Count(p => local.Products.All(l => l.ShopId != p.ShopId || l.ProductId != p.ProductId)),
            incoming.Products.Count(p => local.Products.Any(l => l.ShopId == p.ShopId && l.ProductId == p.ProductId)), rewritten);
    }
    internal static string Restore(ClientStore store, BusinessRestorePlan plan, bool overwrite)
    {
        var previous = store.Business.Read();
        if (previous.Revision != plan.LocalRevision) throw new InvalidOperationException("预览后资料发生变化，请重新预览。");
        var folder = Path.Combine(store.Root, "business-backups"); Directory.CreateDirectory(folder);
        var rollback = Export(store, Path.Combine(folder, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".qnbak"), previous);
        T[] Merge<T>(T[] current, T[] incoming, Func<T, string> key) => current.Where(c => !overwrite || incoming.All(i => key(i) != key(c)))
            .Concat(incoming.Where(i => overwrite || current.All(c => key(c) != key(i)))).ToArray();
        store.Business.Update(local => local with
        {
            Shops = Merge(local.Shops, plan.Incoming.Shops, s => s.Id),
            Products = Merge(local.Products, plan.Incoming.Products, p => p.ShopId + ":" + p.ProductId),
            Policies = Merge(local.Policies, plan.Incoming.Policies, p => p.SellerId),
            Advice = Merge(local.Advice, plan.Incoming.Advice, a => a.SellerId + ":" + a.ProductId),
            Candidates = Merge(local.Candidates, plan.Incoming.Candidates, c => c.Id),
            Rules = Merge(local.Rules, plan.Incoming.Rules, r => r.Id),
            LearningEnabled = local.Shops.Length == 0 ? plan.Incoming.LearningEnabled : local.LearningEnabled
        }, plan.LocalRevision);
        store.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.BusinessRestored, new() { Count = plan.Incoming.Products.Length, Success = true });
        return rollback;
    }
    private static void CheckSecrets(ClientStore store, BusinessState state)
    {
        var secrets = new List<string>();
        try
        {
            var settings = store.Settings();
            foreach (var value in new[] { settings.AiSecret, settings.JevSecret, settings.OnlineKnowledgeSecret })
            {
                if (value.Length >= 8) secrets.Add(value);
                try { var plain = SecretVault.Unprotect(value); if (plain.Length >= 8) secrets.Add(plain); } catch (Exception e) when (e is not OutOfMemoryException) { }
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(state, ClientStore.Json));
        void Scan(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object) foreach (var property in node.EnumerateObject()) Scan(property.Value);
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Scan(item);
            else if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString()!;
                if (secrets.Any(s => value.Contains(s, StringComparison.Ordinal)) || Regex.IsMatch(value, @"(?i)(?:sk|rk|key)-[A-Za-z0-9_-]{12,}|(?:api[_-]?key|access[_-]?token|authorization|password)\s*[:=]\s*[^\s]{6,}"))
                    throw new InvalidDataException("业务文本中发现疑似密钥，请先从商品或学习资料中移除后再备份/恢复。配置文件和密钥本身不会进入业务包。");
            }
        }
        Scan(json.RootElement);
    }
}
