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
            if (args.Length == 2 && args[0] == "--ai-configure-openrouter")
            {
                Console.WriteLine(Json.Serialize(AiShadowRuntime.ConfigureOpenRouter(args[1])));
                return 0;
            }
            if (args.Length == 1 && args[0] == "--ai-provider-smoke")
                return await new AiShadowRuntime().TestProviderAsync();
            if (args.Length == 1 && args[0] == "--ai-preflight") { Console.WriteLine(Json.Serialize(new AiShadowRuntime().Preflight())); return 0; }
            if (args.Length == 4 && args[0] == "--ai-shadow") return await RunAiShadowAsync(args[1], args[2], args[3]);
            if (args.Length == 4 && args[0] == "--dispatch-shadow") return await RunDispatchShadowAsync(args[1], args[2], args[3]);
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

        private static async Task<int> RunDispatchShadowAsync(string envelopePath, string identityPath, string output)
        {
            var manifest = VerifySourceSnapshot();
            var envelope = Json.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath));
            var identity = Json.Deserialize<QnbotIdentityMapping>(File.ReadAllText(identityPath));
            if (!identity.IsTestMapping) throw new InvalidDataException("Shadow dispatch accepts isolated test identities only.");
            var items = new QnocrMessageAdapter().Convert(envelope, identity, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"), DateTime.Now, "qnocr-stable-shadow-v14");
            if (items.Count == 0) throw new InvalidDataException("No confirmed message.");
            Directory.CreateDirectory(output);
            var completed = new TaskCompletionSource<int>();
            var callbacks = 0;
            var coordinator = new BuyerMessageBurstCoordinator(lease =>
            {
                System.Threading.Interlocked.Increment(ref callbacks);
                completed.TrySetResult(lease.Burst.Items.Count);
                return Task.CompletedTask;
            });
            foreach (var item in items) coordinator.Enqueue(item.BurstItem);
            if (await Task.WhenAny(completed.Task, Task.Delay(8000)) != completed.Task) throw new TimeoutException("No coordinator acknowledgement.");
            var count = await completed.Task;
            if (Guard.ForbiddenCalls != 0 || count != items.Count) throw new InvalidOperationException("Shadow dispatch boundary/count mismatch.");
            File.WriteAllText(Path.Combine(output, "qnbot-messages.json"), Json.Serialize(items.Select(i => i.BurstItem.Message).ToArray()));
            var receipt = new { Mode = "ShadowDispatchOnly", Status = "Converted", Reason = (string)null,
                ConvertedMessages = count, AdapterResult = "Converted", DispatchResult = "Accepted", CoordinatorCreated = true,
                CoordinatorCallbacks = callbacks, AiInvoked = false, AiEnabled = false, SendEnabled = false, SentMessages = 0,
                SendEntryInvoked = false, ForbiddenBusinessCalls = Guard.ForbiddenCalls, RuntimeInitializers = Guard.SuppressedInitializers,
                SourceFilesVerified = manifest.Sources.Length, QnbotBaselineCommit = manifest.QnbotCommit, IsTestIdentity = true };
            File.WriteAllText(Path.Combine(output, "shadow-adapter-result.json"), Json.Serialize(receipt));
            Console.WriteLine(Json.Serialize(receipt));
            coordinator.CancelBuyer(identity.SellerNick, identity.BuyerNick, "shadow_dispatch_complete");
            return 0;
        }

        private static async Task<int> RunAiShadowAsync(string envelopePath, string identityPath, string output)
        {
            var manifest = VerifySourceSnapshot();
            var envelope = Json.Deserialize<MessageEnvelope>(File.ReadAllText(envelopePath));
            var identity = Json.Deserialize<QnbotIdentityMapping>(File.ReadAllText(identityPath));
            if (!identity.IsTestMapping) throw new InvalidDataException("V1.3 requires explicit isolated test identity mapping.");
            var items = new QnocrMessageAdapter().Convert(envelope, identity, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"), DateTime.Now, "qnocr-ai-shadow-v13");
            if (items.Count == 0) throw new InvalidDataException("No confirmed messages.");
            // Preview authorization applies only inside this isolated host. The coordinator's runtime
            // Robot remains disabled, and its send/RPA dependencies are throwing test replacements.
            foreach (var item in items)
            {
                item.BurstItem.SafetyDecision.ShouldCallAi = true;
                item.BurstItem.SafetyDecision.Note = "Explicit AI Shadow preview authorization; Send OFF";
            }
            Directory.CreateDirectory(output);
            var ai = new AiShadowRuntime();
            var before = ai.AiCalls;
            var completion = new TaskCompletionSource<string>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int callbacks = 0;
            using (var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(120)))
            {
                var coordinator = new BuyerMessageBurstCoordinator(async lease =>
                {
                    System.Threading.Interlocked.Increment(ref callbacks);
                    try
                    {
                        await ai.ObserveAsync(identity.SellerNick, identity.BuyerNick, lease.Burst.CombinedQuestion, envelope.ProductId, deadline.Token);
                        var answer = await ai.GenerateAsync(identity.SellerNick, identity.BuyerNick, lease.Burst.CombinedQuestion, deadline.Token);
                        completion.TrySetResult(answer);
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                });
                foreach (var item in items) coordinator.Enqueue(item.BurstItem);
                string reply = null; string failure = null;
                try
                {
                    if (await Task.WhenAny(completion.Task, Task.Delay(125000)) != completion.Task) throw new TimeoutException("AI Shadow budget exhausted.");
                    reply = await completion.Task;
                    if (string.IsNullOrWhiteSpace(reply) || reply.StartsWith("错误：", StringComparison.Ordinal)) failure = reply ?? "Empty AI result";
                }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                var calls = ai.AiCalls - before;
                var receipt = new
                {
                    Mode = "AiShadowPreviewOnly", Status = failure == null ? "Converted" : "AiFailed", Reason = failure,
                    AiEnabled = true, SendEnabled = false, AiInvoked = calls > 0, AiCalls = calls, LastAiError = ai.LastAiError,
                    ReplySuggestion = failure == null ? reply : null, ConvertedMessages = failure == null ? items.Count : 0,
                    Input = envelope.Messages, ProductId = envelope.ProductId, ProductBinding = envelope.ProductBinding,
                    SalesAgent = ai.SalesObservation, KnowledgeV2 = ai.KnowledgeObservation, GenerationTrace = ai.GenerationTrace(),
                    AiDurationMilliseconds = ai.GenerationMilliseconds,
                    ProductContextUsage = "Logged OCR sidecar; existing generation service has no product-ID prompt parameter. No fabricated product URL or prompt rewrite.",
                    CoordinatorCreated = true, CoordinatorCallbacks = callbacks, ForbiddenBusinessCalls = Guard.ForbiddenCalls,
                    SendEntryInvoked = false, SentMessages = 0, RuntimeInitializers = Guard.SuppressedInitializers,
                    IsTestIdentity = true, SourceFilesVerified = manifest.Sources.Length, QnbotBaselineCommit = manifest.QnbotCommit,
                    DurationMilliseconds = timer.ElapsedMilliseconds, GenerationService = "Unmodified qnbot StreamingBuyerAnswerService.GetAnswerAsync",
                    Boundary = "Isolated actual coordinator source plus original Bot library generation method; no Bot App/QN/delivery entry startup."
                };
                File.WriteAllText(Path.Combine(output, "qnbot-messages.json"), Json.Serialize(items.Select(i => i.BurstItem.Message).ToArray()));
                File.WriteAllText(Path.Combine(output, "shadow-adapter-result.json"), Json.Serialize(receipt));
                Console.WriteLine(Json.Serialize(receipt));
                coordinator.CancelBuyer(identity.SellerNick, identity.BuyerNick, "ai_shadow_complete");
                if (Guard.ForbiddenCalls != 0) throw new InvalidOperationException("Isolated coordinator attempted forbidden business operation.");
                return failure == null ? 0 : 3;
            }
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
