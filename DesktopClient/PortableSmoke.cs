using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QnocrPoc.Shadow;

namespace QNAgent;

// Synthetic, offline checks for the actual distributed binaries; never opens existing user data.
internal static class PortableSmoke
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + name);
            Console.WriteLine("PASS: " + name); count++;
        }
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        using (var process = Process.GetCurrentProcess())
        {
            var clr = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            Check(clr.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase), ".NET runtime loaded from package");
        }
        var scratch = Path.Combine(root, "SmokeData", Guid.NewGuid().ToString("N"));
        var store = new ClientStore(scratch);
        var settings = store.Settings();
        Check(settings.AiSecret.Length == 0 && settings.JevSecret.Length == 0 && settings.OnlineKnowledgeSecret.Length == 0 && settings.SellerId.Length == 0,
            "new profile has no keys or shop");
        Check(store.Products().Count == 0 && !File.Exists(Path.Combine(scratch, "inbox.json")), "no products or chat history seeded");
        var empty = store.Business.Read();
        Check(empty.Shops.Length == 0 && empty.Policies.Length == 0 && empty.Advice.Length == 0 && empty.Candidates.Length == 0 && empty.Rules.Length == 0,
            "new business profile has no shop, advice, learning samples or learned rules");
        const string key = "synthetic-portable-check";
        var protectedKey = SecretVault.Protect(key);
        Check(protectedKey != key && SecretVault.Unprotect(protectedKey) == key, "Windows key encryption round trip");
        store.SaveSettings(settings with { AiSecret = protectedKey, JevSecret = protectedKey });
        var shop = store.Business.CreateShop("便携版合成测试店");
        store.SaveProducts(new[] { new CatalogProduct { SellerId = shop.Id, ProductId = "123", Name = "合成测试收纳盒", Approved = true } });
        var product = store.Products().Single();
        store.Business.Rename(shop.Id, "合成测试店改名");
        Check(store.Products().Single().ShopId == shop.Id, "shop display rename keeps permanent identity");
        var candidate = new LearningCandidate { ShopId = shop.Id, ProductId = product.ProductId, SourceKey = "synthetic-portable-source", SampleHash = "synthetic",
            ProductHash = LearningService.ProductHash(product), Question = "合成清洁问题", HumanReply = "可用干软布轻擦。", Proposal = "可用干软布轻擦。", Evidence = "可用干软布轻擦。", Kind = "Knowledge", Status = "Pending" };
        store.Business.Update(s => s with { Candidates = new[] { candidate } });
        var learning = new LearningService(store);
        Check(learning.ForReply(product, candidate.Question).Length == 0, "unconfirmed learning never becomes reply evidence");
        learning.Confirm(candidate.Id, candidate.Question, candidate.Proposal);
        Check(learning.ForReply(product, candidate.Question).Length == 1, "explicit confirmation activates scoped experience");
        var backup = BusinessBackupService.Export(store, Path.Combine(scratch, "synthetic.qnbak"));
        using (var zip = ZipFile.OpenRead(backup))
        {
            using var reader = new StreamReader(zip.GetEntry("business.json")!.Open()); var payload = reader.ReadToEnd();
            Check(!payload.Contains(key) && !payload.Contains(protectedKey), "business backup excludes plain and encrypted keys");
        }
        var restored = new ClientStore(Path.Combine(scratch, "restored"));
        BusinessBackupService.Restore(restored, BusinessBackupService.Preview(restored, backup), false);
        Check(restored.Products().Single().ShopId == shop.Id && restored.Settings().AiSecret.Length == 0 &&
            new LearningService(restored).ForReply(restored.Products().Single(), candidate.Question).Length == 1,
            "portable restore retains identity and approved experience without credentials");
        using (var db = new SqliteConnection("Data Source=:memory:"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT 1";
            Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "native SQLite available");
        }
        Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using var worker = await MemoryNativeWorker.StartAsync(timeout.Token);
            byte[] png;
            using (var bitmap = new Bitmap(800, 200))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    using var font = new Font("Microsoft YaHei", 32);
                    graphics.DrawString("测试商品 HELLO 12345", font, Brushes.Black, 20, 60);
                }
                using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); png = stream.ToArray();
            }
            var first = await worker.RecognizeAsync(png, "memory:portable-fixture", timeout.Token);
            var second = await worker.RecognizeAsync(png, "memory:portable-fixture", timeout.Token);
            Check(first.Text.Contains("12345") && first.Text.Contains("商品") && second.Text.Contains("12345"), "packaged worker and local OCR model recognize Chinese and digits twice");
            using var process = Process.GetProcessById(worker.ProcessId);
            var clr = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            Check(clr.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase), "worker uses packaged .NET runtime");
            var vc = process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase) || m.ModuleName.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(vc.All(m => m.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)), "loaded Visual C++ libraries are app-local");
        }).GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(store) { ShowActivated = false, WindowState = WindowState.Minimized };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); window.Close(); };
        window.Loaded += (_, _) => timer.Start();
        app.Run(window);
        Check(File.Exists(Path.Combine(scratch, "app-status.json")), "WPF main window loaded and closed");
        var report = DiagnosticExporter.Export(store, Path.Combine(scratch, "diagnostics.zip"), DateTimeOffset.UtcNow.AddDays(-1));
        Check(File.Exists(report.Path), "diagnostic ZIP export works");
        Console.WriteLine($"Passed {count} portable checks. No AI requests or live chat capture.");
        return 0;
    }
}
