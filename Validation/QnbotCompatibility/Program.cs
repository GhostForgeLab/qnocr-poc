using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using QnocrPoc.Integration;
using QnocrPoc.Integration.Wire;
using Bot.ChromeNs;

namespace CompatibilityHost
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private static int Main(string[] args)
        {
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
        private static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 4 && args[0] == "--shadow-convert") return RunShadowConversion(args[1], args[2], args[3]);
            if (args.Length != 3) { Console.Error.WriteLine("Usage: QnbotCompatibility.exe envelope.json identity_mapping.json output-directory"); return 2; }
            var manifest = VerifySourceSnapshot();
            var envelope = Json.Deserialize<MessageEnvelope>(File.ReadAllText(args[0]));
            var identity = Json.Deserialize<QnbotIdentityMapping>(File.ReadAllText(args[1]));
            var timezone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            var adapter = new QnocrMessageAdapter();
            var checks = AdapterChecks.Run(envelope, identity, timezone);
            var converted = adapter.Convert(envelope, identity, timezone, DateTime.Now, "offline-replay-v1");
            if (!identity.IsTestMapping) throw new InvalidDataException("This host accepts test identity mappings only.");
            if (converted.Count == 0) throw new InvalidDataException("No messages to test.");
            Directory.CreateDirectory(args[2]);
            File.WriteAllText(Path.Combine(args[2], "converted-messages.json"), Json.Serialize(converted));
            File.WriteAllText(Path.Combine(args[2], "qnbot-messages.json"), Json.Serialize(converted.Select(item => item.BurstItem.Message).ToArray()));
            var completion = new TaskCompletionSource<BuyerMessageBurstLease>();
            int calls = 0;
            var coordinator = new BuyerMessageBurstCoordinator(lease =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                completion.TrySetResult(lease);
                return Task.CompletedTask;
            });
            foreach (var item in converted) coordinator.Enqueue(item.BurstItem);
            if (await Task.WhenAny(completion.Task, Task.Delay(8000)) != completion.Task) throw new TimeoutException("Actual coordinator did not dispatch to offline callback.");
            var received = await completion.Task;
            if (received.Burst.Items.Count != converted.Count) throw new InvalidDataException("Unexpected burst count; inspect coordinator merge limits.");
            if (received.Burst.Items.Any(item => item.SessionGeneration <= 0) || !received.IsCurrent)
                throw new InvalidOperationException("Actual session state did not accept the converted messages.");
            foreach (var item in adapter.Convert(envelope, identity, timezone, DateTime.Now, "offline-replay-v1")) coordinator.Enqueue(item.BurstItem);
            await Task.Delay(1500);
            if (calls != 1) throw new InvalidOperationException("Duplicate replay was dispatched again.");
            var history = converted.Select(item => IncomingMessageSafety.Evaluate(item.BurstItem.Message, item.BurstItem.DisplayText, DateTime.Now)).ToArray();
            if (Guard.ForbiddenCalls != 0) throw new InvalidOperationException("A forbidden business/transport dependency was invoked.");
            if (Guard.Errors.Count != 0) throw new InvalidOperationException("Coordinator logged unexpected errors: " + string.Join(";", Guard.Errors));
            var result = new
            {
                Runtime = Environment.Version.ToString(),
                TargetFramework = Assembly.GetExecutingAssembly().GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>().FrameworkName,
                Mode = "OfflineCompatibilityOnly",
                QnbotBaselineCommit = manifest.QnbotCommit, SourceFilesVerified = manifest.Sources.Length,
                AdapterChecksPassed = checks,
                TargetMessageType = converted[0].BurstItem.Message.GetType().FullName,
                CoordinatorType = coordinator.GetType().FullName,
                AcceptedItems = received.Burst.Items.Count, CallbackCountAfterDuplicateReplay = calls,
                SessionGenerations = received.Burst.Items.Select(item => item.SessionGeneration).ToArray(),
                CombinedQuestion = received.Burst.CombinedQuestion,
                HistoricalSafetyAllowsAi = history.Select(item => item.ShouldCallAi).ToArray(),
                ProductContext = converted.Select(item => item.ProductIdContext).ToArray(),
                NativeDerivedProductIds = converted.Select(item => item.BurstItem.Message.originalData.itemId).ToArray(),
                ForbiddenBusinessCalls = Guard.ForbiddenCalls,
                SuppressedRuntimeInitializers = Guard.SuppressedInitializers,
                QnbotCoreSourceUnmodified = true,
                Boundary = "Real coordinator/session/safety/model source compiled unchanged; peripheral runtime dependencies are isolated test replacements. Not a running-Bot integration."
            };
            File.WriteAllText(Path.Combine(args[2], "compatibility-result.json"), Json.Serialize(result));
            Console.WriteLine(Json.Serialize(result));
            coordinator.CancelBuyer(identity.SellerNick, identity.BuyerNick, "offline_test_complete");
            return 0;
        }

        private static int RunShadowConversion(string envelopePath, string identityPath, string output)
        {
            var manifest = VerifySourceSnapshot();
            var envelope = Json.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath));
            var identity = identityPath == "-" ? null : Json.Deserialize<QnbotIdentityMapping>(File.ReadAllText(identityPath));
            Directory.CreateDirectory(output);
            string status;
            string reason = null;
            int count = 0;
            try
            {
                // Pure conversion only: do not construct a coordinator, run safety policy, or invoke a callback.
                var items = new QnocrMessageAdapter().Convert(envelope, identity,
                    TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"), DateTime.Now, "qnocr-shadow-v11");
                File.WriteAllText(Path.Combine(output, "qnbot-messages.json"), Json.Serialize(items.Select(item => item.BurstItem.Message).ToArray()));
                File.WriteAllText(Path.Combine(output, "converted-messages.json"), Json.Serialize(items));
                count = items.Count;
                status = "Converted";
            }
            catch (InvalidDataException ex) { status = "Rejected"; reason = ex.Message; }
            if (Guard.ForbiddenCalls != 0 || Guard.SuppressedInitializers != 0)
                throw new InvalidOperationException("Unexpected runtime/business initialization in conversion-only mode.");
            var receipt = new
            {
                Mode = "ShadowConversionOnly", Status = status, Reason = reason,
                ConvertedMessages = count, CoordinatorCreated = false,
                ForbiddenBusinessCalls = Guard.ForbiddenCalls, RuntimeInitializers = Guard.SuppressedInitializers,
                IsTestIdentity = identity != null && identity.IsTestMapping,
                IdentityBinding = "OperatorProvided; current conversation identity not automatically verified",
                DisplayTimeZone = "China Standard Time", QnbotBaselineCommit = manifest.QnbotCommit,
                SourceFilesVerified = manifest.Sources.Length
            };
            File.WriteAllText(Path.Combine(output, "shadow-adapter-result.json"), Json.Serialize(receipt));
            Console.WriteLine(Json.Serialize(receipt));
            return 0;
        }

        private static SourceManifest VerifySourceSnapshot()
        {
            SourceManifest manifest;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QnbotSourceManifest.json"))
            using (var reader = new StreamReader(stream)) manifest = Json.Deserialize<SourceManifest>(reader.ReadToEnd());
            var sourceRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../.tools/qnbot-compat-source"));
            foreach (var file in manifest.Sources)
            {
                using (var sha = SHA256.Create())
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(sourceRoot, file.Path)))).Replace("-", "");
                    if (actual != file.Sha256) throw new InvalidDataException("Source snapshot differs from embedded build manifest: " + file.Path);
                }
            }
            return manifest;
        }
        private sealed class SourceManifest { public string QnbotCommit { get; set; } public SourceFile[] Sources { get; set; } }
        private sealed class SourceFile { public string Path { get; set; } public string Sha256 { get; set; } }
    }
}
