using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CompatibilityHost
{
    // Loads the unchanged generation service as a library. Never constructs App/QN or initializes delivery.
    internal sealed partial class AiShadowRuntime
    {
        private readonly Assembly bot;
        private readonly string root;
        public AiShadowRuntime()
        {
            root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../.tools/ai-shadow"));
            var runtime = Path.Combine(root, "runtime");
            var manifest = new JavaScriptSerializer().Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "manifest.json")));
            if (manifest.SendEnabled) throw new InvalidDataException("AI Shadow requires Send OFF.");
            foreach (var f in manifest.RuntimeFiles)
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(runtime, f.Path)))).Replace("-", "") != f.Sha256)
                        throw new InvalidDataException("AI runtime hash mismatch.");
            File.Copy(Path.Combine(runtime, "default-business-policy.json"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "default-business-policy.json"), true);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var path = Path.Combine(runtime, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var lib = Assembly.LoadFrom(Path.Combine(runtime, "BotLib.dll"));
            var paths = lib.GetType("BotLib.Extensions.PathEx", true);
            var data = Path.Combine(root, "data-root");
            SetPath(paths, "_userDataRoot", data);
            SetPath(paths, "_globalDataDir", Path.Combine(data, "data"));
            SetPath(paths, "_legacyDataDir", Path.Combine(data, "data"));
            SetPath(paths, "_startUpPathOfExe", runtime);
            SetPath(paths, "_parentPathOfExePath", root);
            bot = Assembly.LoadFrom(Path.Combine(runtime, "Bot.exe"));
            // Supply an isolated settings scope without calling ShopSettingsScope.Enter, whose UI binding
            // initializer is intentionally outside this host. No production seller identity is inferred.
            var shop = Activator.CreateInstance(bot.GetType("Bot.ShopScope.ShopContext", true),
                "qnocr_ai_shadow_test", "qianniu", "seller-test", "offline_fixture_seller", false);
            var scope = bot.GetType("Bot.ShopScope.ShopSettingsScope", true).GetField("CurrentValue", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            scope.GetType().GetProperty("Value").SetValue(scope, shop, null);
            Call("Bot.ShopScope.ShopScopedParamBridge", "InitializeForApp");
            // These are settings on the isolated copy, not edits to qnbot logic or production settings.
            Call("Bot.Params+Robot", "SetIsAutoReply", false);
            var rules = Call("Bot.ChromeNs.BotFeatureStore", "GetAutoReplyRules");
            rules.GetType().GetProperty("EnableHandoffNotification").SetValue(rules, false, null);
            Call("Bot.ChromeNs.BotFeatureStore", "SaveAutoReplyRules", rules);
            if ((bool)Call("Bot.Params+Robot", "GetIsAutoReply")) throw new InvalidOperationException("Send flag was not disabled.");
        }
        private static void SetPath(Type type, string name, string path)
        {
            type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Path.GetFullPath(path).TrimEnd('\\') + "\\");
        }
        public object Call(string type, string method, params object[] args)
        {
            var methods = bot.GetType(type, true).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == method && m.GetParameters().Length == args.Length).ToArray();
            if (methods.Length != 1) throw new MissingMethodException(type, method);
            return methods[0].Invoke(null, args);
        }
        public object Preflight()
        {
            var endpoints = ((IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints")).Cast<object>().ToArray();
            return new { Mode = "AiShadowPreflight", AiEnabled = true, SendEnabled = false,
                EndpointCount = endpoints.Length,
                Endpoints = endpoints.Select(e => new { Model = e.GetType().GetProperty("TextModel").GetValue(e, null),
                    Host = SafeHost((string)e.GetType().GetProperty("BaseUrl").GetValue(e, null)) }).ToArray(),
                Runtime = "Unmodified qnbot StreamingBuyerAnswerService", DataRoot = Path.Combine(root, "data-root"),
                DeliveryEntryInvoked = false, BotApplicationStarted = false };
        }
        private static string SafeHost(string url) { Uri parsed; return Uri.TryCreate(url, UriKind.Absolute, out parsed) ? parsed.Host : "Invalid"; }
        public async Task<string> GenerateAsync(string seller, string buyer, string question, CancellationToken token)
        {
            foreach (var endpoint in (IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints"))
            {
                var url = (string)endpoint.GetType().GetProperty("BaseUrl").GetValue(endpoint, null);
                RequireOpenRouter(url);
            }
            if ((bool)Call("Bot.Params+Robot", "GetIsAutoReply")) throw new InvalidOperationException("Send must remain OFF.");
            var rules = Call("Bot.ChromeNs.BotFeatureStore", "GetAutoReplyRules");
            if ((bool)rules.GetType().GetProperty("EnableHandoffNotification").GetValue(rules, null)) throw new InvalidOperationException("Notifications must remain OFF.");
            var method = bot.GetType("Bot.ChromeNs.StreamingBuyerAnswerService", true).GetMethod("GetAnswerAsync", BindingFlags.Public | BindingFlags.Static);
            var task = (Task<string>)method.Invoke(null, new object[] { seller, buyer, question, token, new Action<string>(_ => { }), null });
            return await task;
        }
        public long AiCalls { get { var stats = Call("Bot.ChromeNs.BotRuntimeStats", "GetSnapshot"); return Convert.ToInt64(stats.GetType().GetProperty("TotalAiCalls").GetValue(stats, null)); } }
        public string LastAiError
        {
            get
            {
                var stats = Call("Bot.ChromeNs.BotRuntimeStats", "GetSnapshot");
                var error = (string)stats.GetType().GetProperty("LastError").GetValue(stats, null) ?? "";
                foreach (var endpoint in (IEnumerable)Call("Bot.ChromeNs.AiEndpointStore", "GetEnabledEndpoints"))
                {
                    var key = (string)endpoint.GetType().GetProperty("ApiKey").GetValue(endpoint, null);
                    if (!string.IsNullOrEmpty(key)) error = error.Replace(key, "[REDACTED]");
                }
                return error;
            }
        }
        private sealed class Manifest { public bool SendEnabled { get; set; } public RuntimeFile[] RuntimeFiles { get; set; } }
        private sealed class RuntimeFile { public string Path { get; set; } public string Sha256 { get; set; } }
    }
}
