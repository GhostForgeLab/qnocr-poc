using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Bot.ChromeNs;
using QnocrPoc.Integration;
using QnocrPoc.Integration.Wire;

namespace CompatibilityHost
{
    internal static class AdapterChecks
    {
        public static int Run(MessageEnvelope input, QnbotIdentityMapping identity, TimeZoneInfo zone)
        {
            var json = new JavaScriptSerializer();
            Func<MessageEnvelope> clone = () => json.Deserialize<MessageEnvelope>(json.Serialize(input));
            var adapter = new QnocrMessageAdapter();
            var observed = DateTime.Now;
            Func<MessageEnvelope, System.Collections.Generic.IReadOnlyList<QnocrAdaptedMessage>> convert = e => adapter.Convert(e, identity, zone, observed, "contract-checks");
            int passed = 0;
            Action<bool, string> check = (ok, description) => { if (!ok) throw new InvalidOperationException("Check failed: " + description); passed++; Console.WriteLine("PASS " + description); };
            var converted = convert(input);
            check(converted.Count == input.Messages.Count, "one native message per envelope message");
            check(converted.All(x => x.BurstItem.Message.IsBuyerSend && x.BurstItem.SellerNick == identity.SellerNick && x.BurstItem.BuyerNick == identity.BuyerNick), "actual qnbot buyer direction and nickname routing");
            check(converted.Select(x => x.BurstItem.Message.MessageText).SequenceEqual(input.Messages.Select(x => x.Text)), "native message text preserved before coordinator normalization");
            check(converted.All(x => x.BurstItem.ReceivedAt == observed), "observation time is separate from source sendTime");
            check(converted.All(x => !x.BurstItem.SafetyDecision.ShouldCallAi && x.BurstItem.VisionDecision.Kind == VisionDecisionKind.Skip), "offline reply flags stay disabled");
            check(converted.All(x => x.ProductIdContext == input.ProductId && x.BurstItem.Message.originalData.actionUrl == null && x.BurstItem.Message.originalData.url == null), "product context retained without fabricated buyer product links");
            check(converted.Select(x => x.BurstItem.MessageKey).SequenceEqual(convert(input).Select(x => x.BurstItem.MessageKey)), "identical replay generates stable synthetic keys");
            check(converted.All(x => x.BurstItem.MessageKey == IncomingMessageSafety.BuildMessageKey(x.BurstItem.Message, x.BurstItem.DisplayText)), "keys use actual qnbot key builder");
            check(converted.All(x => x.BurstItem.SortValue == IncomingMessageSafety.GetSortValue(x.BurstItem.Message)), "sort values use actual qnbot timestamp parser");
            var changedProduct = clone(); changedProduct.ProductId = "1000000000002";
            check(converted.Select(x => x.BurstItem.MessageKey).SequenceEqual(convert(changedProduct).Select(x => x.BurstItem.MessageKey)), "changing visible product does not create new message identities");
            var repeated = clone(); repeated.Messages = new System.Collections.Generic.List<EnvelopeMessage> { input.Messages[0], input.Messages[0] };
            check(convert(repeated).Select(x => x.BurstItem.MessageKey).Distinct().Count() == 2, "same timestamp/text occurrences within a batch have distinct local keys");
            Action<Action<MessageEnvelope>, string> reject = (change, description) =>
            {
                var invalid = clone(); change(invalid);
                try { convert(invalid); } catch (InvalidDataException) { check(true, description); return; }
                check(false, description);
            };
            reject(e => e.SellerId = "", "empty seller ID is not guessed");
            reject(e => e.BuyerId = "wrong", "unmapped buyer ID is rejected");
            reject(e => e.SchemaVersion = 2, "unknown schema is rejected");
            reject(e => e.Messages[0].Role = "seller", "seller text cannot become buyer input");
            reject(e => e.Messages[0].Timestamp = "not-a-time", "invalid source time is rejected");
            var noTargets = json.Deserialize<QnbotIdentityMapping>(json.Serialize(identity)); noTargets.SellerTargetId = noTargets.BuyerTargetId = null;
            check(adapter.Convert(input, noTargets, zone, observed, "contract-checks").All(x => x.BurstItem.Message.fromid.targetId == ""), "opaque envelope IDs are not guessed as platform targetId");
            return passed;
        }
    }
}
