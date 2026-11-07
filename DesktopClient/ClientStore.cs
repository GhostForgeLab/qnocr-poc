using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QNAgent;

public sealed record ClientSettings
{
    public string SellerId { get; init; } = "";
    public string AiEndpoint { get; init; } = "https://openrouter.ai/api/v1";
    public string AiModel { get; init; } = "openai/gpt-5.5";
    public string AiSecret { get; init; } = "";
    public string JevEndpoint { get; init; } = "https://api.typesafe.ai/v1/systemone";
    public string JevModel { get; init; } = "jev-latest";
    public string JevSecret { get; init; } = "";
    public string OnlineKnowledgeUrl { get; init; } = "";
    public string OnlineKnowledgeSecret { get; init; } = "";
    // Old PollSeconds JSON is ignored; upgraded installations use the faster default.
    public int CaptureIntervalMilliseconds { get; init; } = 500;
    public bool FastRecognition { get; init; } = true;
    public DiagnosticLogLevel LogLevel { get; init; } = DiagnosticLogLevel.Information;
}

public sealed record CatalogProduct
{
    public string ShopId { get; init; } = "";
    public string SellerId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Name { get; init; } = "";
    public string SalesScope { get; init; } = "";
    public string Material { get; init; } = "";
    public string Capacity { get; init; } = "";
    public string ShippingTime { get; init; } = "";
    public string ReturnPolicy { get; init; } = "";
    public string CurrentPrice { get; init; } = "";
    public string CustomizationPolicy { get; init; } = "";
    public string Notes { get; init; } = "";
    public ProductVariant[] Variants { get; init; } = Array.Empty<ProductVariant>();
    public Promotion[] Promotions { get; init; } = Array.Empty<Promotion>();
    public ProductFaq[] Faqs { get; init; } = Array.Empty<ProductFaq>();
    public string Source { get; init; } = "人工录入";
    public bool Approved { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public override string ToString() => $"{Name} · {ProductId} · {(Approved ? "已核对" : "待核对")}";
    public IReadOnlyDictionary<string, string> Fields() => new Dictionary<string, string>
    {
        ["SalesScope"] = SalesScope, ["Material"] = Material, ["Capacity"] = Capacity, ["ShippingTime"] = ShippingTime,
        ["ReturnPolicy"] = ReturnPolicy, ["CurrentPrice"] = CurrentPrice,
        ["CustomizationPolicy"] = CustomizationPolicy
    };
}

public sealed record InboxMessage(string SessionId, string MessageId, string SellerId, string Buyer,
    string Text, DateTime Timestamp, string ProductId, string CaptureId, bool Baseline)
{
    public string Role { get; init; } = "buyer";
    public long Sequence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => $"{(Role == "seller" ? "卖家" : "买家")} · {Timestamp:HH:mm:ss}\n{Text}";
    public string ContentKind { get; init; } = "Text";
    public string WindowScopeId { get; init; } = "";
    public override string ToString() => $"{Timestamp:MM-dd HH:mm:ss}  {Buyer}\n{Text}";
}

public sealed class ClientStore
{
    public string Root { get; }
    internal string DiagnosticRoot { get; }
    internal DiagnosticLog Diagnostics => DiagnosticLog.ForRoot(DiagnosticRoot);
    internal BusinessRepository Business => new(this);
    public string ReviewRoot => Path.Combine(Root, "reviews");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public ClientStore(string root, string? diagnosticRoot = null)
    { Root = Path.GetFullPath(root); DiagnosticRoot = Path.GetFullPath(diagnosticRoot ?? root); Directory.CreateDirectory(Root); }
    public ClientSettings Settings() => Read<ClientSettings>("settings.json") ?? new();
    public List<CatalogProduct> Products() => Business.Read().Products.ToList();
    public List<InboxMessage> Messages() => Read<List<InboxMessage>>("inbox.json") ?? new();
    public T? ReadValue<T>(string name) => Read<T>(name);
    public void SaveSettings(ClientSettings settings) { Write("settings.json", settings); Diagnostics.Configure(settings.LogLevel); }
    public ShopKnowledge? Shop(string sellerId)
    {
        var state = Business.Read(); var identity = state.Shops.SingleOrDefault(s => s.Id == sellerId || s.BindingKey == sellerId);
        return state.Policies.SingleOrDefault(s => s.SellerId == (identity?.BindingKey ?? sellerId));
    }
    public void SaveShop(ShopKnowledge shop)
    {
        CatalogValidation.ValidateShop(shop);
        var identity = Business.Resolve(shop.SellerId); shop = shop with { SellerId = identity.BindingKey };
        Business.Update(s => s with { Policies = s.Policies.Where(p => p.SellerId != shop.SellerId).Append(shop).ToArray() });
    }
    public void SaveProducts(IEnumerable<CatalogProduct> products)
    {
        var items = products.ToList();
        if (items.Count > 2000) throw new InvalidDataException("商品资料最多保存 2000 条。");
        foreach (var item in items) Validate(item);
        if (items.GroupBy(item => (item.SellerId, item.ProductId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("商品资料中有重复的店铺和商品 ID。");
        var normalized = items.Select(p => { var identity = Business.Resolve(p.SellerId); return p with { SellerId = identity.BindingKey, ShopId = identity.Id }; }).ToArray();
        Business.Update(s => s with { Products = normalized,
            Advice = s.Advice.Where(a => normalized.Any(p => p.SellerId == a.SellerId && p.ProductId == a.ProductId)).ToArray(),
            Candidates = s.Candidates.Where(c => normalized.Any(p => p.ShopId == c.ShopId && p.ProductId == c.ProductId)).ToArray(),
            Rules = s.Rules.Where(r => normalized.Any(p => p.ShopId == r.ShopId && p.ProductId == r.ProductId)).ToArray() });
    }
    public int Import(string json, string source)
    {
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new InvalidDataException("资料文件不能超过 2 MB。");
        var incoming = JsonSerializer.Deserialize<List<CatalogProduct>>(json, Json)
            ?? throw new InvalidDataException("需要商品 JSON 数组。");
        foreach (var item in incoming) Validate(item);
        incoming = incoming.Select(p => { var shop = Business.Resolve(p.SellerId); return p with { SellerId = shop.BindingKey, ShopId = shop.Id }; }).ToList();
        if (incoming.GroupBy(item => (item.SellerId, item.ProductId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("导入文件存在重复商品。");
        var items = Products();
        foreach (var item in incoming)
        {
            // External content never preserves or grants approval, including replacement of an existing product.
            items.RemoveAll(prior => prior.SellerId == item.SellerId && prior.ProductId == item.ProductId);
            items.Add(item with { Approved = false, Source = source, UpdatedAt = DateTimeOffset.Now });
        }
        SaveProducts(items);
        return incoming.Count;
    }
    public void SaveMessages(IEnumerable<InboxMessage> messages) => Write("inbox.json", messages.TakeLast(2000).ToArray());
    private T? Read<T>(string name) => File.Exists(Path.Combine(Root, name))
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Root, name)), Json) : default;
    public void Write<T>(string name, T value)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Validate(CatalogProduct product)
    {
        CatalogValidation.ValidateDetails(product);
        if (string.IsNullOrWhiteSpace(product.SellerId) || product.SellerId.Length > 128 ||
            string.IsNullOrWhiteSpace(product.ProductId) || product.ProductId.Length > 32 || !product.ProductId.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 200 || product.Notes is null || product.Notes.Length > 12000 ||
            string.IsNullOrWhiteSpace(product.Source) || product.Source.Length > 2048 || product.Fields().Values.Any(value => value is null || value.Length > 2000))
            throw new InvalidDataException("请填写店铺、数字商品 ID 和名称；商品备注最多 12000 字，单项资料最多 2000 字。");
    }
}

internal static class SecretVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(string text) => string.IsNullOrEmpty(text) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string text) => string.IsNullOrEmpty(text) ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(text), false));
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "QNAgent", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            Array.Clear(bytes);
        }
    }
}
