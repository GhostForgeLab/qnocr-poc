using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Bot.ChatRecord;
using Bot.ChromeNs;
using QnocrPoc.Integration.Wire;

namespace QnocrPoc.Integration
{
    public sealed class QnbotIdentityMapping
    {
        public string SellerId { get; set; }
        public string SellerNick { get; set; }
        public string BuyerId { get; set; }
        public string BuyerNick { get; set; }
        public string SellerTargetId { get; set; }
        public string BuyerTargetId { get; set; }
        public bool IsTestMapping { get; set; }
    }

    internal sealed class QnocrAdaptedMessage
    {
        public BuyerMessageBurstItem BurstItem { get; set; }
        public string ProductIdContext { get; set; }
        public string ProductBinding { get; set; }
        public string SourceTimestamp { get; set; }
        public string SourceSellerId { get; set; }
        public string SourceBuyerId { get; set; }
        public bool IsTestIdentity { get; set; }
        public string Mode { get { return "OfflineCompatibilityOnly"; } }
    }

    // Compiled beside the ACTUAL qnbot internal types in the isolated compatibility host.
    // Deliberately excluded from the main .NET 8 POC and never installed in qnbot in V1.0.
    internal sealed class QnocrMessageAdapter
    {
        public IReadOnlyList<QnocrAdaptedMessage> Convert(MessageEnvelope envelope,
            QnbotIdentityMapping identity, TimeZoneInfo displayedTimeZone, DateTime receivedAt, string replayScope)
        {
            if (envelope == null || envelope.SchemaVersion != 1 || envelope.Source != "Qianniu.OCR" || envelope.Messages == null || envelope.ProductId == null || envelope.ProductBinding == null)
                throw new InvalidDataException("Expected Qianniu.OCR MessageEnvelope schema v1.");
            if (identity == null || string.IsNullOrWhiteSpace(envelope.SellerId) || string.IsNullOrWhiteSpace(envelope.BuyerId) ||
                !string.Equals(envelope.SellerId, identity.SellerId, StringComparison.Ordinal) ||
                !string.Equals(envelope.BuyerId, identity.BuyerId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(identity.SellerNick) || string.IsNullOrWhiteSpace(identity.BuyerNick) ||
                identity.SellerNick.Contains("#") || identity.BuyerNick.Contains("#") ||
                string.Equals(identity.SellerNick.Trim(), identity.BuyerNick.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Explicit, matching seller/buyer ID-to-qnbot-nick mappings are required; unknown IDs are not guessed.");
            if (displayedTimeZone == null || receivedAt == default(DateTime) || string.IsNullOrWhiteSpace(replayScope))
                throw new ArgumentException("Explicit display timezone, observation time and replay scope are required.");

            var result = new List<QnocrAdaptedMessage>();
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in envelope.Messages)
            {
                if (item == null || item.Role != "buyer" || string.IsNullOrWhiteSpace(item.Text))
                    throw new InvalidDataException("Only nonempty confirmed buyer text can be adapted.");
                DateTime time;
                if (!DateTime.TryParseExact(item.Timestamp,
                    new[] { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK" },
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || time == default(DateTime))
                    throw new InvalidDataException("Message has no valid ISO timestamp.");
                DateTime utc;
                if (time.Kind == DateTimeKind.Unspecified)
                {
                    if (displayedTimeZone.IsInvalidTime(time) || displayedTimeZone.IsAmbiguousTime(time))
                        throw new InvalidDataException("Ambiguous or invalid display-zone timestamp requires explicit resolution.");
                    utc = TimeZoneInfo.ConvertTimeToUtc(time, displayedTimeZone);
                }
                else utc = time.ToUniversalTime();
                var utcTime = new DateTimeOffset(utc);
                var baseKey = Hash(replayScope, envelope.Source, envelope.SellerId, envelope.BuyerId,
                    utcTime.ToString("O", CultureInfo.InvariantCulture), item.Text);
                int occurrence;
                occurrences.TryGetValue(baseKey, out occurrence);
                occurrences[baseKey] = occurrence + 1;
                var messageId = baseKey + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
                var native = new QNChatMessage
                {
                    templateId = 0, // Unknown OCR template; no forged platform type or native message ID.
                    mcode = new Mcode { clientId = "qnocr-offline-v1", messageId = messageId },
                    cid = new Cid { ccode = "qnocr:" + Hash(envelope.SellerId, envelope.BuyerId) },
                    fromid = new Fromid { nick = identity.BuyerNick.Trim(), targetId = identity.BuyerTargetId ?? string.Empty },
                    toid = new Toid { nick = identity.SellerNick.Trim(), targetId = identity.SellerTargetId ?? string.Empty },
                    loginid = new Loginid { nick = identity.SellerNick.Trim(), targetId = identity.SellerTargetId ?? string.Empty },
                    originalData = new OriginalData { text = item.Text },
                    summary = item.Text,
                    sendTime = utcTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                    sortTimeMicrosecond = ((utc.Ticks - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 10).ToString(CultureInfo.InvariantCulture)
                };
                result.Add(new QnocrAdaptedMessage
                {
                    ProductIdContext = envelope.ProductId ?? string.Empty,
                    ProductBinding = envelope.ProductBinding ?? "UnverifiedOcrContext",
                    SourceTimestamp = item.Timestamp,
                    SourceSellerId = envelope.SellerId, SourceBuyerId = envelope.BuyerId,
                    IsTestIdentity = identity.IsTestMapping,
                    BurstItem = new BuyerMessageBurstItem
                    {
                        SellerNick = native.toid.nick, BuyerNick = native.fromid.nick,
                        Message = native, DisplayText = item.Text,
                        MessageKey = IncomingMessageSafety.BuildMessageKey(native, item.Text),
                        SortValue = IncomingMessageSafety.GetSortValue(native), ReceivedAt = receivedAt,
                        SafetyDecision = new IncomingMessageDecision { ShouldCallAi = false, MessageLabel = item.Text, Note = "Offline compatibility; no reply authorization" },
                        VisionDecision = new VisionMessageDecision { Kind = VisionDecisionKind.Skip, QuestionLabel = item.Text, Note = "Offline observation callback only" }
                    }
                });
            }
            return result.AsReadOnly();
        }

        private static string Hash(params string[] fields)
        {
            // Length-prefix avoids separator collisions; product context intentionally does not alter message identity.
            var value = new StringBuilder();
            foreach (var field in fields) value.Append((field ?? "").Length).Append(':').Append(field ?? "");
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
