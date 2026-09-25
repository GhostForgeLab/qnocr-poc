using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CompatibilityHost
{
    internal sealed partial class AiShadowRuntime
    {
        public object SalesObservation { get; private set; }
        public object KnowledgeObservation { get; private set; }
        public long GenerationMilliseconds { get; private set; }

        public async Task ObserveAsync(string seller, string buyer, string question, string productId, CancellationToken token)
        {
            var settings = bot.GetType("Bot.SalesAgent.SalesAgentSettings", true);
            settings.GetField("EnableSalesAgent").SetValue(null, true);
            settings.GetField("SalesAgentShadowMode").SetValue(null, true);
            settings.GetField("EnableRuntimeProductIdentityObserver").SetValue(null, false);
            var request = Activator.CreateInstance(bot.GetType("Bot.SalesAgent.Models.SalesRequest", true));
            Action<string, object> set = (name, value) => request.GetType().GetProperty(name).SetValue(request, value, null);
            set("ShopKey", "qnocr_ai_shadow_test"); set("SellerNick", seller); set("BuyerNick", buyer);
            set("Question", question); set("ExternalItemId", productId);
            var service = Activator.CreateInstance(bot.GetType("Bot.SalesAgent.Services.SalesAgentService", true));
            var lines = new List<string>();
            var observe = bot.GetType("Bot.SalesAgent.Services.SalesAgentShadowObserver", true)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "ObserveAsync" && m.GetParameters().Length == 4);
            await (Task)observe.Invoke(null, new object[] { request, service, new Action<string>(line => lines.Add(line)), token });
            SalesObservation = new { Invoked = true, Service = "Unmodified SalesAgentShadowObserver / SalesAgentService", Records = lines.ToArray(),
                ProductHint = "OCR external item ID; no confirmed internal ProductProfile binding fabricated" };

            // Enable the existing V2 observer only in the copied test scope, preserving its thresholds.
            // Its decision is logged, never substituted for the existing generation service's prompt/routing.
            var view = Call("Bot.Knowledge.KnowledgeEngineV2Service", "GetSettingsView", seller);
            var threshold = (double)view.GetType().GetProperty("DirectThreshold").GetValue(view, null);
            var confidence = (double)view.GetType().GetProperty("MinConfidence").GetValue(view, null);
            Call("Bot.Knowledge.KnowledgeEngineV2Service", "SetSettings", seller, true, "shadow", threshold, confidence);
            KnowledgeObservation = Call("Bot.Knowledge.KnowledgeEngineV2Service", "Resolve", seller, buyer, question);
        }

        public object[] GenerationTrace()
        {
            // Read pending in-process trace records without starting qnbot's remote-upload timer.
            var states = bot.GetType("Bot.ChromeNs.MessageProcessingTraceService", true)
                .GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var records = new List<object>();
            foreach (var state in (IEnumerable)states.GetType().GetProperty("Values").GetValue(states, null))
            foreach (var item in (IEnumerable)state.GetType().GetField("Pending").GetValue(state))
            {
                Func<string, object> get = name => item.GetType().GetField(name).GetValue(item);
                records.Add(new { Stage = get("Stage"), Status = get("Status"), Summary = get("Summary"), Detail = get("Detail"), DurationMs = get("DurationMs") });
            }
            return records.ToArray();
        }
    }
}
