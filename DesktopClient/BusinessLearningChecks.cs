using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QnocrPoc.Models;

namespace QNAgent;

internal static class BusinessLearningChecks
{
    private sealed class Fake : ILearningClient
    {
        public int Calls; public string Context = "";
        public Func<Task<string>> Response = () => Task.FromResult(JsonSerializer.Serialize(new { kind = "Knowledge", proposal = "可用干软布轻擦。", reason = "补充明确的清洁方法。", evidence = "可用干软布轻擦。" }));
        public Task<string> AnalyzeAsync(ClientSettings settings, string context, CancellationToken token) { Calls++; Context = context; return Response(); }
    }
    private static T Field<T>(object obj, string key) => (T)obj.GetType().GetField(key, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(obj)!;
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = RunAsync(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } }; timer.Start(); Dispatcher.PushFrame(frame);
        try { return task.GetAwaiter().GetResult(); } finally { app.Shutdown(); }
    }
    private static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS: " + label); }
        void Reject(Action action, string label)
        { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or JsonException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
        var root = Path.GetFullPath(Path.Combine("Output", "BusinessLearningChecks", Guid.NewGuid().ToString("N")));
        var source = new ClientStore(Path.Combine(root, "source"));
        var legacy = new CatalogProduct { SellerId = "旧店铺", ProductId = "111", Name = "测试收纳盒", Material = "合金", Approved = true };
        source.Write("products.json", new[] { legacy }); source.Write("shop-knowledge.json", new[] { new ShopKnowledge { SellerId = legacy.SellerId, Approved = true } });
        var original = File.ReadAllBytes(Path.Combine(source.Root, "products.json"));
        var p = source.Products().Single(); var id = p.ShopId;
        Check(Guid.TryParseExact(id, "N", out _) && source.Business.Read().Shops.Single().BindingKey == legacy.SellerId, "legacy catalog migrates to persistent shop identity");
        Check(File.ReadAllBytes(Path.Combine(source.Root, "products.json")).SequenceEqual(original), "migration leaves original business files intact");
        source.Business.Rename(id, "修改后的名称");
        Check(new ClientStore(source.Root).Products().Single().ShopId == id && source.Business.Resolve(id).BindingKey == legacy.SellerId && source.Shop(id)!.Approved,
            "rename and restart preserve products, shop policy and legacy connection alias");
        source.Import(JsonSerializer.Serialize(new[] { p with { SellerId = id, Name = "通过固定ID更新" } }), "fixture");
        Check(source.Products().Count == 1 && !source.Products().Single().Approved, "JSON import recognizes fixed ID without duplicating product or granting approval");
        source.SaveProducts(new[] { p }); p = source.Products().Single();
        var secondShop = source.Business.CreateShop("修改后的名称");
        Reject(() => source.Business.Resolve("修改后的名称"), "duplicate display names require explicit stable ID");
        Check(source.Business.Resolve(secondShop.Id).Id != id, "same display name never merges distinct shops");
        var encrypted = SecretVault.Protect("test-only-private-token-987654321");
        source.SaveSettings(new ClientSettings { AiSecret = encrypted, JevSecret = encrypted, OnlineKnowledgeSecret = encrypted });
        source.Write("inbox.json", new[] { new { Buyer = "PRIVATE_BUYER_MARKER", Text = "PRIVATE_CHAT_MARKER" } });
        source.Write("window-bindings.json", new[] { new { ScopeId = "PRIVATE_WINDOW_MARKER" } });
        var q = new InboxMessage("session-one", "question-one", p.SellerId, "PRIVATE_BUYER_MARKER", "如何清洁？", DateTime.Now.AddSeconds(-30), p.ProductId, "capture", false) { WindowScopeId = "scope-one", Sequence = 1 };
        var seller = q with { MessageId = "seller-one", Role = "seller", Text = "可用干软布轻擦。", Timestamp = q.Timestamp.AddSeconds(25), Sequence = 2 };
        AssistedReplyView Preview(InboxMessage question, string text = "这个问题需核实。") => new("preview", question.SessionId, "internal-buyer", question.Buyer, "Observed", Array.Empty<ProductReference>(),
            question.Text, text, null, text, DateTimeOffset.Now.AddSeconds(-20), "Low", "", "AIGenerated", "hash", null, null, null, null, null, null, "NotSent", null, null, null,
            ProductKnowledgeVersion: p.UpdatedAt.ToString("O"), CurrentProductId: p.ProductId, KnowledgeCategory: "HUMAN_CHECK");
        var fake = new Fake(); var service = new LearningService(source, fake);
        var c = service.Collect(q, seller, p, Preview(q), new[] { q, seller }) ?? throw new Exception("Sample not collected");
        Check(c.Status == "PendingAnalysis" && source.Business.Read().Rules.Length == 0, "human difference is collected as pending only");
        Check(service.ForReply(p, q.Text).Length == 0, "unreviewed samples cannot enter reply evidence");
        Check(service.Collect(q, seller, p, Preview(q), new[] { q, seller }) is null, "duplicate capture is deduplicated");
        Check(service.Collect(q, seller with { Baseline = true }, p, Preview(q), new[] { q, seller }) is null, "baseline history is not mined");
        Check(service.Collect(q, seller with { WindowScopeId = "other-window" }, p, Preview(q), new[] { q, seller }) is null, "other window cannot be paired");
        Check(service.Collect(q, seller with { Buyer = "other-buyer" }, p, Preview(q), new[] { q, seller }) is null, "other buyer cannot be paired");
        Check(service.Collect(q, seller with { SessionId = "other-session" }, p, Preview(q), new[] { q, seller }) is null, "other session cannot be paired");
        Check(service.Collect(q, seller, p, Preview(q, seller.Text), new[] { q, seller }) is null, "adopted AI suggestion does not become self-training evidence");
        Check(service.Collect(q, seller with { ContentKind = "Unrecognized" }, p, Preview(q), new[] { q, seller }) is null, "image and unclear OCR do not become facts");
        Check(service.Collect(q, seller, p, Preview(q) with { CurrentProductId = "222" }, new[] { q, seller }) is null, "reply for another product cannot be learned");
        Check(service.Collect(q, seller, p, Preview(q) with { KnowledgeCategory = "REPLY_UNAVAILABLE" }, new[] { q, seller }) is null, "service failures are not treated as valid AI examples");
        Check(service.Collect(q, seller, p, Preview(q), new[] { q, q with { MessageId = "new-question", Timestamp = q.Timestamp.AddSeconds(10) }, seller }) is null, "new intervening buyer question invalidates old pairing");
        var seller2 = seller with { MessageId = "seller-two", Text = "不要用硬刷。", Timestamp = seller.Timestamp.AddSeconds(1), Sequence = 3 };
        var combined = service.Collect(q, seller2, p, Preview(q), new[] { q, seller, seller2 })!;
        Check(combined.Id == c.Id && combined.HumanReply.Contains("不要用硬刷") && source.Business.Read().Candidates.Length == 1, "consecutive seller fragments update one pending sample");
        await service.AnalyzeAsync(c.Id, CancellationToken.None, c.SampleHash);
        Check(fake.Calls == 0, "debounced stale sample does not call model");
        await service.AnalyzeAsync(c.Id, CancellationToken.None, combined.SampleHash);
        c = source.Business.Read().Candidates.Single();
        Check(c.Status == "Pending" && c.Proposal == seller.Text && fake.Calls == 1 && source.Business.Read().Rules.Length == 0, "analysis proposes grounded original text but still requires human confirmation");
        Reject(() => LearningService.Parse("{\"kind\":\"Knowledge\",\"proposal\":\"完全防水。\",\"reason\":\"猜测\",\"evidence\":\"可用干软布轻擦。\"}", c), "invented answer is rejected even when evidence quote is real");
        var special = LearningService.Parse(JsonSerializer.Serialize(new { kind = "Knowledge", proposal = "这次便宜10元。", reason = "", evidence = "这次便宜10元。" }), c with { HumanReply = "这次便宜10元。" });
        Check(special.Kind == "CaseSpecific", "one-off price promise cannot auto-become universal knowledge");
        service.Confirm(c.Id, c.Question, c.Proposal);
        Check(service.ForReply(p, q.Text).Single().Content == seller.Text && source.Business.Read().Candidates.Single().Status == "Approved", "explicit confirmation activates product-scoped experience");
        Check(source.Products().Single().Faqs.Length == 0 && source.Products().Single().Material == p.Material, "learning confirmation never overwrites structured product facts");
        Reject(() => service.Confirm(c.Id, c.Question, c.Proposal), "same candidate cannot be confirmed twice");
        Check(service.Collect(q, seller2 with { Text = "后续补充" }, p, Preview(q), new[] { q, seller, seller2 with { Text = "后续补充" } }) is null, "later fragments cannot overwrite reviewed sample");
        Check(service.ForReply(p with { ShopId = secondShop.Id }, q.Text).Length == 0 && service.ForReply(p with { ProductId = "222" }, q.Text).Length == 0, "confirmed experience never crosses shops or products");
        Check(service.ForReply(p with { Material = "新材质" }, q.Text).Length == 0, "changed product facts pause old experience");
        var rule = source.Business.Read().Rules.Single(); service.SetActive(rule.Id, false);
        Check(service.ForReply(p, q.Text).Length == 0, "manual disable removes evidence immediately");
        service.SetActive(rule.Id, true);
        var advice = new KnowledgeAdviceState { SellerId = p.SellerId, ProductId = p.ProductId, Ignored = new[] { new IgnoredKnowledgeSuggestion("cleaning", "怎样清洁？", DateTimeOffset.Now) } };
        source.Business.Update(s => s with { Advice = new[] { advice } });
        var backup = BusinessBackupService.Export(source, Path.Combine(root, "business.qnbak"));
        using (var zip = ZipFile.OpenRead(backup))
        {
            Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "business.json", "manifest.json" }), "backup has only whitelisted business and manifest entries");
            using var reader = new StreamReader(zip.GetEntry("business.json")!.Open()); var body = reader.ReadToEnd();
            Check(!body.Contains(encrypted) && !body.Contains("test-only-private-token") && !body.Contains("PRIVATE_CHAT_MARKER") && !body.Contains("PRIVATE_WINDOW_MARKER") && !body.Contains("PRIVATE_BUYER_MARKER"), "backup excludes keys, raw chat, buyer identifier and window bindings");
        }
        var destination = new ClientStore(Path.Combine(root, "new-computer"));
        var plan = BusinessBackupService.Preview(destination, backup);
        Check(plan.NewProducts == 1 && plan.NewShops == 2 && plan.ConflictingProducts == 0, "new-computer restore preview counts additions");
        var rollback = BusinessBackupService.Restore(destination, plan, false);
        Check(File.Exists(rollback) && destination.Products().Single().ShopId == id && destination.Settings().AiSecret == "", "restore retains stable ID and requires fresh keys on new computer");
        Check(destination.Business.Read().Advice.Single().Ignored.Length == 1 && new LearningService(destination).ForReply(destination.Products().Single(), q.Text).Length == 1, "ignored advice and confirmed learning survive migration");
        destination.SaveSettings(new ClientSettings { AiSecret = encrypted, AiModel = "local-model" });
        destination.SaveProducts(new[] { destination.Products().Single() with { Material = "本机材质" } });
        plan = BusinessBackupService.Preview(destination, backup);
        Check(plan.ConflictingProducts == 1, "restore reports conflicts before applying");
        BusinessBackupService.Restore(destination, plan, false);
        Check(destination.Products().Single().Material == "本机材质" && destination.Settings().AiModel == "local-model" && destination.Settings().AiSecret == encrypted, "default conflict handling preserves local facts and credentials");
        plan = BusinessBackupService.Preview(destination, backup); BusinessBackupService.Restore(destination, plan, true);
        Check(destination.Products().Single().Material == p.Material, "explicit overwrite restores backed-up facts");
        plan = BusinessBackupService.Preview(destination, backup); destination.Business.Rename(id, "预览后更改");
        Reject(() => BusinessBackupService.Restore(destination, plan, true), "stale preview cannot overwrite concurrent edits");
        var unrelated = new ClientStore(Path.Combine(root, "unrelated")); unrelated.SaveProducts(new[] { legacy with { ProductId = "222" } });
        plan = BusinessBackupService.Preview(unrelated, backup); Check(plan.RenamedBindings == 1, "same legacy name with different stable ID is a separate shop");
        BusinessBackupService.Restore(unrelated, plan, false);
        Check(unrelated.Products().Select(x => x.ShopId).Distinct().Count() == 2, "restore never merges unrelated same-name shops");
        var tampered = Path.Combine(root, "tampered.qnbak"); File.Copy(backup, tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update)) { zip.GetEntry("business.json")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("business.json").Open()); writer.Write("{}"); }
        Reject(() => BusinessBackupService.Preview(destination, tampered), "payload hash mismatch blocks restore");
        var extra = Path.Combine(root, "extra.qnbak"); File.Copy(backup, extra);
        using (var zip = ZipFile.Open(extra, ZipArchiveMode.Update)) zip.CreateEntry("../settings.json");
        Reject(() => BusinessBackupService.Preview(destination, extra), "extra or traversal entries rejected without extraction");
        Reject(() => BusinessRepository.Validate(source.Business.Read() with { Products = new CatalogProduct[] { null! } }), "null product rejected safely");
        Reject(() => BusinessRepository.Validate(source.Business.Read() with { Candidates = new[] { c with { HumanReply = null! } } }), "null candidate text rejected safely");
        source.SaveProducts(new[] { p with { Notes = "test-only-private-token-987654321" } });
        Reject(() => BusinessBackupService.Export(source, Path.Combine(root, "leak.qnbak")), "known credential pasted into business text blocks export");
        source.SaveProducts(new[] { p });
        // Sample minimization and asynchronous review isolation.
        var q2 = q with { MessageId = "question-two", Text = "PRIVATE_BUYER_MARKER 怎么清洁？" };
        var s2 = seller with { Text = "可用干软布轻擦。联系 13812345678，订单 123456789012345678，test-only-private-token-987654321" };
        var redacted = service.Collect(q2, s2, p, Preview(q2), new[] { q2, s2 })!;
        Check(!redacted.Question.Contains("PRIVATE_BUYER") && !redacted.HumanReply.Contains("13812345678") && !redacted.HumanReply.Contains("123456789012345678") && !redacted.HumanReply.Contains("test-only-private-token"), "learning minimizes identity, phone, order and known secrets before persistence");
        var held = new TaskCompletionSource<string>(); fake.Response = () => held.Task;
        var analysis = service.AnalyzeAsync(redacted.Id, CancellationToken.None); service.Ignore(redacted.Id);
        held.SetResult(JsonSerializer.Serialize(new { kind = "Knowledge", proposal = "可用干软布轻擦。", reason = "", evidence = "可用干软布轻擦。" })); await analysis;
        Check(source.Business.Read().Candidates.Single(x => x.Id == redacted.Id).Status == "Ignored", "late analysis cannot overwrite ignored decision");
        source.Business.Update(s => s with { LearningEnabled = false });
        Check(service.Collect(q with { MessageId = "off" }, seller, p, Preview(q), new[] { q, seller }) is null, "learning switch prevents collection");
        // WPF pages use actual controls, and all fixtures are synthetic.
        destination.Business.Update(s => s with { Candidates = s.Candidates.Append(c with { Id = Guid.NewGuid().ToString("N"), SourceKey = "ui-sample", Status = "Pending" }).ToArray() });
        var window = new MainWindow(destination);
        try
        {
            window.Width = 1220; window.Height = 820;
            var host = Field<ContentControl>(window, "_page");
            foreach (var page in new[] { "_businessPage", "_learningPage" })
            {
                host.Content = Field<UIElement>(window, page); window.Show(); window.UpdateLayout();
                if (page == "_learningPage") { Field<StackPanel>(window, "_learningItems").Children.OfType<Expander>().First().IsExpanded = true; window.UpdateLayout(); }
                var bitmap = new RenderTargetBitmap(1220, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(root, page + ".png")); encoder.Save(file);
            }
            Check(Field<ListBox>(window, "_shopList").Items.Count == 2 && Field<StackPanel>(window, "_learnedItems").Children.Count == 1, "WPF shop and learning pages render migrated data");
        }
        finally { window.Close(); }
        Console.WriteLine($"Business/learning checks passed: {passed}. Fixtures: {root}"); return 0;
    }
}
