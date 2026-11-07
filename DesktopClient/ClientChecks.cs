using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

namespace QNAgent;

internal static class ClientChecks
{
    private sealed class ClipboardProbe : IAssistantClipboard
    {
        public bool Fail { get; set; }
        public string Text { get; private set; } = "";
        public int Calls { get; private set; }
        public void SetText(string text)
        {
            Calls++;
            if (Fail) throw new IOException("controlled clipboard contention");
            Text = text;
        }
    }
    public static async Task<int> Run(string root)
    {
        var output = Path.Combine(root, "Output", "DesktopChecks", Guid.NewGuid().ToString("N"));
        var store = new ClientStore(output); int count = 0;
        void Check(bool pass, string name) { if (!pass) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
        var key = "local-test-secret"; var cipher = SecretVault.Protect(key);
        Check(cipher != key && SecretVault.Unprotect(cipher) == key, "Windows secret protection round-trip");
        store.SaveSettings(new ClientSettings { SellerId = "shop-a", AiSecret = cipher });
        Check(!File.ReadAllText(Path.Combine(output, "settings.json")).Contains(key) && store.Settings().SellerId == "shop-a", "settings persist without plaintext key");
        var product = new CatalogProduct { SellerId = "shop-a", ProductId = "12345678901", Name = "检查商品", Material = "人工核对材质", Approved = true };
        store.SaveProducts(new[] { product });
        Check(store.Products().Single().Approved, "manual product approval persists");
        store.Import(JsonSerializer.Serialize(new[] { product with { Material = "新资料" } }), "controlled-import");
        Check(!store.Products().Single().Approved && store.Products().Single().Material == "新资料", "import revokes old approval even if payload says approved");
        Reject(() => store.Import(JsonSerializer.Serialize(new[] { product, product }), "duplicate"), "duplicate product import rejected");
        Reject(() => store.Import(JsonSerializer.Serialize(new[] { product with { ProductId = "../bad" } }), "invalid"), "invalid product identity rejected");
        Check(store.Products().Single().Material == "新资料", "failed import leaves prior catalog intact");
        var detailed = product with
        {
            Variants = new[] { new ProductVariant { Name = "粗支烟盒-款式 A", Price = 298m, Details = "粗支" }, new ProductVariant { Name = "细支烟盒-款式 B", Price = 328.50m, Details = "细支" }, new ProductVariant { Name = "待定款式", Price = null } },
            Faqs = new[] { new ProductFaq { Question = "不同规格有什么区别？", Answer = "A 适用于粗支，B 适用于细支。" } },
            Promotions = new[] { new Promotion { Title = "单品券", AppliesTo = "仅款式 A", Rule = "满 200 减 10", Validity = "2026-10-01 至 2026-10-31", Stacking = "不可叠加店铺券" } }
        };
        store.SaveProducts(new[] { detailed });
        var saved = store.Products().Single();
        Check(saved.Variants[0].Price == 298m && saved.Variants[1].Price == 328.50m && saved.Variants[2].Price is null && saved.Faqs[0].Answer.Contains("粗支"), "variant price mapping, unknown price and FAQ survive persistence");
        store.Import(JsonSerializer.Serialize(new[] { saved }), "round-trip");
        Check(!store.Products().Single().Approved && store.Products().Single().Promotions[0].AppliesTo == "仅款式 A", "structured JSON import retains scope and revokes approval");
        Reject(() => store.SaveProducts(new[] { detailed with { Variants = new[] { new ProductVariant { Name = "A", Price = -1 } } } }), "negative variant price rejected");
        Reject(() => store.SaveProducts(new[] { detailed with { Variants = new[] { new ProductVariant { Name = "A", Price = 1.001m } } } }), "excess price precision rejected");
        Reject(() => store.SaveProducts(new[] { detailed with { Variants = new[] { new ProductVariant { Name = " A " }, new ProductVariant { Name = "A" } } } }), "duplicate variant names rejected");
        Reject(() => store.SaveProducts(new[] { detailed with { Faqs = new[] { new ProductFaq { Question = "问题" } } } }), "incomplete FAQ rejected");
        Reject(() => store.Import("[{\"SellerId\":\"shop-a\",\"ProductId\":\"123\",\"Name\":\"bad\",\"Variants\":null}]", "malformed"), "null structured collections rejected");
        var shop = new ShopKnowledge { SellerId = "shop-a", Approved = true, Promotions = new[] { detailed.Promotions[0] with { Title = "店铺券", AppliesTo = "全店（定制款除外）", Rule = "满 500 减 30" } } };
        store.SaveShop(shop); store.SaveShop(shop with { SellerId = "shop-b", Promotions = new[] { shop.Promotions[0] with { Title = "其他店优惠" } } });
        Check(store.Shop("shop-a")!.Promotions[0].Title == "店铺券" && store.Shop("unknown") is null, "shop promotions are independently persisted and seller-scoped");
        Reject(() => store.SaveShop(shop with { Promotions = new[] { new Promotion { Title = "缺条件" } } }), "promotion conditions must be explicit");
        var variantsOnly = detailed with { Faqs = Array.Empty<ProductFaq>(), Promotions = Array.Empty<Promotion>() };
        Check(CatalogValidation.HasKnowledge(variantsOnly, "CurrentPrice") && CatalogValidation.HasKnowledge(variantsOnly, "Capacity") &&
            !CatalogValidation.HasKnowledge(variantsOnly with { Variants = new[] { new ProductVariant { Name = "待定款式" } } }, "CurrentPrice"), "structured-only variant knowledge satisfies guards while unknown prices stay missing");
        Reject(() => Connections.Endpoint("http://example.com/api"), "remote plaintext HTTP rejected");
        Reject(() => Connections.Endpoint("https://example.com/api?key=secret"), "URL credential query rejected");
        using var context = new AgentContextPipeline(Path.Combine(output, "context.db"));
        var envelope = new MessageEnvelope("Qianniu.OCR", "shop-a", "check-buyer", "", new[] { new EnvelopeMessage("buyer", "这款是什么材质？", DateTime.Now) });
        var session = context.ProcessMessage(envelope); var message = session.Messages.Single();
        var inbox = new InboxMessage(session.SessionId, message.MessageId, "shop-a", "check-buyer", message.Content, message.Timestamp, "", "controlled", false);
        var view = AgentContextIntentAdapter.Build(session, message.MessageId, DateTimeOffset.Now);
        using (var prompt = JsonDocument.Parse(Connections.Prompt(inbox, view with { ProductId = detailed.ProductId }, detailed, shop)))
        {
            var facts = prompt.RootElement.GetProperty("Product");
            Check(facts.GetProperty("Variants")[1].GetProperty("Price").GetDecimal() == 328.50m &&
                facts.GetProperty("Promotions")[0].GetProperty("Title").GetString() == "单品券" &&
                prompt.RootElement.GetProperty("ShopPromotions").GetProperty("Promotions")[0].GetProperty("Title").GetString() == "店铺券", "model payload separates variant prices, single-product offers and shop offers");
        }
        Reject(() => Connections.Prompt(inbox, view with { ProductId = detailed.ProductId }, detailed, shop with { SellerId = "shop-b" }), "cross-store promotion payload rejected");
        Reject(() => Connections.Prompt(inbox, view with { ProductId = detailed.ProductId }, detailed, shop with { Approved = false }), "unapproved shop promotion payload rejected");
        var replies = new ReplyService(store, jev: new FixtureJevGateway());
        var guarded = await replies.GenerateAsync(inbox, view, null, store.Settings(), CancellationToken.None);
        Check(guarded.KnowledgeGuardApplied && guarded.KnowledgeStatus == "Missing", "missing product creates local guarded preview without HTTP");
        Check(replies.Latest(inbox)?.ReplyPreviewId == guarded.ReplyPreviewId, "review index survives service reconstruction");
        var clipboard = new ClipboardProbe { Fail = true };
        try { replies.ConfirmAndCopy(inbox, guarded.ReplyPreviewId, "需要先核实具体款式。", clipboard); throw new Exception("expected clipboard failure"); }
        catch (IOException) { Check(replies.Latest(inbox)?.Status == "Approved", "clipboard failure retains approval for retry"); }
        clipboard.Fail = false;
        var copied = replies.ConfirmAndCopy(inbox, guarded.ReplyPreviewId, "需要先核实具体款式。", clipboard);
        Check(copied.Status == "Copied" && clipboard.Text == "需要先核实具体款式。", "retry copies approved reply without approving twice");
        replies.ConfirmAndCopy(inbox, copied.ReplyPreviewId, copied.EffectiveReply, clipboard);
        Check(clipboard.Calls == 3 && clipboard.Text == copied.EffectiveReply, "repeated confirmation refreshes clipboard with current exact reply");
        try { replies.Assistant.Copy(copied.ReplyPreviewId, "check", new string('0', 64), clipboard, allowRepeat: true); throw new Exception("expected stale hash rejection"); }
        catch (InvalidOperationException) { Check(clipboard.Calls == 3, "repeat copy still validates exact approved hash"); }
        var revised = replies.SaveDraft(inbox, copied.ReplyPreviewId, "修改后：请确认您咨询的是粗支款还是细支款。");
        Check(revised.ReplyPreviewId != copied.ReplyPreviewId && revised.Status == "HumanEdited" &&
            replies.Assistant.Show(copied.ReplyPreviewId)!.EffectiveReply == copied.EffectiveReply, "editing a copied reply creates an unapproved revision and preserves original");
        var revisedCopy = replies.ConfirmAndCopy(inbox, revised.ReplyPreviewId, revised.EffectiveReply, clipboard);
        Check(clipboard.Text == revised.EffectiveReply && revisedCopy.Status == "Copied", "edited revision is separately confirmed and copied");
        Reject(() => Connections.Prompt(inbox, view, product with { SellerId = "shop-b" }), "cross-store knowledge cannot reach model");
        var visual = await replies.GenerateAsync(inbox with { ContentKind = "Unrecognized" }, view, product,
            store.Settings() with { AiEndpoint = "http://127.0.0.1:1", AiModel = "must-not-call" }, CancellationToken.None);
        Check(visual.AiReply.Contains("文字说明") && !visual.KnowledgeUsedByAi, "unrecognized image produces local clarification without model/image upload");

        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var server = Task.Run(async () =>
        {
            for (var request = 0; request < 4; request++)
            {
                var incoming = await listener.GetContextAsync();
                var body = incoming.Request.HttpMethod == "GET" ? JsonSerializer.Serialize(new[] { product })
                    : "{\"choices\":[{\"message\":{\"content\":\"受控服务回复\"}}]}";
                if (incoming.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(incoming.Request.InputStream);
                    var json = JsonDocument.Parse(await reader.ReadToEndAsync());
                    using var prompt = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                    var facts = prompt.RootElement.GetProperty("Product");
                    if (facts.GetProperty("Fields").GetProperty("Material").GetString() != "人工核对材质" &&
                        facts.GetProperty("Notes").GetString() != "材质：人工核对材质")
                        throw new Exception("actual HTTP body lacks current approved knowledge");
                    if (facts.GetProperty("Variants").GetArrayLength() > 0 &&
                        (facts.GetProperty("Variants")[1].GetProperty("Price").GetDecimal() != 328.50m ||
                        prompt.RootElement.GetProperty("ShopPromotions").GetProperty("SellerId").GetString() != "shop-a"))
                        throw new Exception("actual HTTP body lost variant price or shop promotion scope");
                }
                incoming.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body); await incoming.Response.OutputStream.WriteAsync(bytes); incoming.Response.Close();
            }
        });
        var config = store.Settings() with { AiEndpoint = $"http://127.0.0.1:{port}/v1", AiModel = "controlled", OnlineKnowledgeUrl = $"http://127.0.0.1:{port}/products" };
        var reply = await replies.GenerateAsync(inbox, view, product, config, CancellationToken.None);
        Check(reply.AiReply == "受控服务回复" && reply.KnowledgeUsedByAi && !reply.KnowledgeGuardApplied, "approved product travels through actual HTTP serializer and existing preview workflow");
        var edited = replies.Assistant.Edit(reply.ReplyPreviewId, "check", "人工修订");
        var approved = replies.Assistant.Approve(reply.ReplyPreviewId, "check", edited.ReplySha256);
        Check(approved.Status == "Approved" && approved.EffectiveReply == "人工修订", "native workflow retains original edit and approval service");
        var notesReply = await replies.GenerateAsync(inbox, view, product with { Material = "", Notes = "材质：人工核对材质" }, config, CancellationToken.None);
        Check(notesReply.KnowledgeUsedByAi && !notesReply.KnowledgeGuardApplied, "reviewed text-document knowledge can answer without duplicate structured entry");
        var detailedReply = await replies.GenerateAsync(inbox, view, detailed, config, CancellationToken.None);
        Check(detailedReply.KnowledgeUsedByAi && detailedReply.AiReply == "受控服务回复", "structured catalog and current shop promotion reach actual HTTP request");
        var synced = await Connections.SyncKnowledgeAsync(store, config, CancellationToken.None);
        Check(synced == 1 && !store.Products().Single().Approved, "online HTTP import reaches local catalog as draft");
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        var logEvents = store.Diagnostics.Snapshot(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, out _);
        Check(logEvents.Any(e => e.Event == DiagnosticEvent.ReplyStarted) && logEvents.Any(e => e.Event == DiagnosticEvent.ReplyFinished) &&
            logEvents.Any(e => e.Event == DiagnosticEvent.ReplyCopied), "actual generation and copy workflow emits technical diagnostic events");
        store.Write("checks.json", new { Passed = count, ExternalAi = false, ActualQianniuCapture = false });
        Console.WriteLine($"Passed {count} desktop integration checks. Evidence: {output}"); return 0;
    }
}
