// Test-only environmental replacements. No Bot.exe, UI, network, AI, or sending implementation is referenced.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bot.ChatRecord;

namespace CompatibilityHost
{
    internal static class Guard
    {
        public static int ForbiddenCalls, SuppressedInitializers, ActiveActivities;
        public static readonly List<string> Errors = new List<string>();
        public static T Block<T>(string operation)
        { Interlocked.Increment(ref ForbiddenCalls); throw new InvalidOperationException("Forbidden offline operation: " + operation); }
    }
}
namespace BotLib
{
    public static class Log
    {
        public static void Info(string message) { }
        public static void ErrorWithMaxCount(string message, int count) { lock (CompatibilityHost.Guard.Errors) CompatibilityHost.Guard.Errors.Add(message); }
        public static void Exception(Exception exception) { lock (CompatibilityHost.Guard.Errors) CompatibilityHost.Guard.Errors.Add(exception.GetType().Name); }
    }
}
namespace DbEntity
{
    public class LocalUser { public string Nick, TargetId; }
    public class Conversation { public string Nick, TargetId, Display, TargetType, Type, ConversationType, Scene, Category, Source, Channel; }
}
namespace Bot.Knowledge
{
    internal static class LocalShortReplyUi { public static void Initialize() { Interlocked.Increment(ref CompatibilityHost.Guard.SuppressedInitializers); } }
    internal static class KnowledgeEngineV2Service
    {
        public static bool IsEnabled(string seller) { return CompatibilityHost.Guard.Block<bool>("Knowledge V2"); }
        public static bool IsSnapshotReady(string seller) { return CompatibilityHost.Guard.Block<bool>("Knowledge V2 snapshot"); }
    }
}
namespace Bot.ShopScope
{
    internal sealed class ShopContext { }
    internal static class ShopContextLocator { public static ShopContext ResolveRuntimeBySellerNick(string seller) { return null; } }
    internal static class ShopSettingsScope { public static IDisposable Enter(ShopContext shop) { return CompatibilityHost.Guard.Block<IDisposable>("shop settings"); } }
}
namespace Bot.ChromeNs
{
    internal static class Params { public static readonly OfflineRobot Robot = new OfflineRobot(); }
    internal sealed class OfflineRobot { public bool CanUseRobotReal { get { return false; } } public bool GetIsAutoReply() { return false; } }
    internal sealed class QN
    {
        public OfflineRpa Rpa { get { return CompatibilityHost.Guard.Block<OfflineRpa>("RPA"); } }
        public static QN FindExistingBySellerNick(string seller) { return CompatibilityHost.Guard.Block<QN>("real QN lookup"); }
        public Task<bool> SendTextWithRetryAsync(string buyer, string answer, int retries, CancellationToken token) { return CompatibilityHost.Guard.Block<Task<bool>>("send"); }
    }
    internal sealed class OfflineRpa { public string GetSendFailureReason() { return CompatibilityHost.Guard.Block<string>("RPA state"); } }
    internal static class BuyerSessionAgentRuntimeBridge
    {
        public static void EnsureStarted() { Interlocked.Increment(ref CompatibilityHost.Guard.SuppressedInitializers); }
        public static void RegisterAcceptedGeneration(string seller, string buyer, long generation, DateTime at) { }
    }
    internal static class BuyerStreamingReplyPipeline { public const int TotalAiBudgetSeconds = 50; }
    internal sealed class BotActivityLease : IDisposable
    {
        private int disposed;
        public BotActivityLease() { Interlocked.Increment(ref CompatibilityHost.Guard.ActiveActivities); }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref CompatibilityHost.Guard.ActiveActivities); }
    }
    internal static class BotActivityCoordinator { public static BotActivityLease Begin(string name, string seller, string buyer) { return new BotActivityLease(); } }
    internal enum AdaptiveDelayKind { DenseBurst, Media, Greeting, Fragment, Complete }
    internal static class AdaptiveReplyTimingService
    {
        public static void RecordInterval(string seller, string buyer, DateTime before, DateTime after) { }
        public static int AdjustDelay(string seller, string buyer, int baseline, AdaptiveDelayKind kind) { return baseline; }
    }
    internal static class ConversationContextStore
    {
        public static void RefreshAndRecord(QNChatMessage message, string text) { }
        public static bool IsPlatformSystemTip(QNChatMessage message, string text) { return false; }
        public static bool IsWithdrawalNotice(QNChatMessage message, string text) { return false; }
        public static bool IsProductLink(QNChatMessage message, string text) { return false; }
        public static void RegisterProductLinkReply(QNChatMessage message, string text) { CompatibilityHost.Guard.Block<object>("product reply registration"); }
        public static bool TryTakeProductLinkReply(string seller, string buyer, string question, out string answer) { answer = null; return CompatibilityHost.Guard.Block<bool>("product fixed reply"); }
    }
    internal static class RechargeStatusAutoQueryService { public static bool TryConsumeHandled(QNChatMessage message, string text, out string note) { note = null; return false; } }
    internal static class VisionImageCacheService
    {
        public static void MarkLatestBuyerImageWithdrawn(QNChatMessage message, string text) { CompatibilityHost.Guard.Block<object>("vision cache"); }
        public static void Prime(QNChatMessage message, string text) { CompatibilityHost.Guard.Block<object>("image download"); }
    }
    internal static class ManualRechargeAssistantService { public static Task<bool> TryHandleAsync(QN qn, BuyerMessageBurstItem item, CancellationToken token) { return CompatibilityHost.Guard.Block<Task<bool>>("recharge"); } }
    internal static class FirstInquiryFixedReplyService
    {
        public static bool TryResolve(string seller, string buyer, string question, out string answer) { answer = null; return CompatibilityHost.Guard.Block<bool>("first fixed reply"); }
        public static bool TryPrepare(string seller, string buyer, QNChatMessage message, string question, IncomingMessageDecision decision, out string answer) { answer = null; return CompatibilityHost.Guard.Block<bool>("first reply prepare"); }
        public static void MarkDelivered(string seller, string buyer) { CompatibilityHost.Guard.Block<object>("delivery mark"); }
        public static void ReleaseReservation(string seller, string buyer, string reason) { CompatibilityHost.Guard.Block<object>("reply reservation"); }
    }
    internal sealed class OfflineRules { public bool EnableWorkHours { get; set; } public string WorkStartTime { get; set; } public string WorkEndTime { get; set; } public string OffHoursFixedText { get; set; } }
    internal sealed class OfflineRuleDecision { public bool Matched { get; set; } }
    internal static class BotFeatureStore
    {
        public static OfflineRules GetAutoReplyRules() { return CompatibilityHost.Guard.Block<OfflineRules>("reply settings"); }
        public static OfflineRuleDecision EvaluateAutoReplyRule(string question) { return CompatibilityHost.Guard.Block<OfflineRuleDecision>("reply rule"); }
    }
    internal static class LocalShortReplyService { public static bool TryResolve(string seller, string question, out string answer, out string phrase) { answer = phrase = null; return CompatibilityHost.Guard.Block<bool>("local reply"); } }
    internal static class BotOutboundMessageFormatter { public static string EnsureAiMarker(string answer) { return CompatibilityHost.Guard.Block<string>("outbound formatting"); } }
    internal sealed class OfflineReplyControl { public void SetSendResult(bool ok, string message) { CompatibilityHost.Guard.Block<object>("send result"); } }
    internal static class ResponseProgressTracker
    {
        public static OfflineReplyControl BeginAnswer(string seller, string buyer, string question, DateTime detected) { return CompatibilityHost.Guard.Block<OfflineReplyControl>("answer UI"); }
        public static OfflineReplyControl SetAnswerReady(string seller, string buyer, string question, string answer, string source, DateTime detected, DateTime ready) { return CompatibilityHost.Guard.Block<OfflineReplyControl>("answer ready"); }
        public static void Complete(string seller, string buyer) { CompatibilityHost.Guard.Block<object>("answer completion UI"); }
        public static void MarkContextualContinuationMerged(string seller, string buyer, DateTime previous, string text) { }
    }
    internal static class KnowledgeLearningService { public static void RegisterAnswerSource(string seller, string buyer, string question, string answer, string source) { CompatibilityHost.Guard.Block<object>("knowledge learning"); } }
    internal static class BotRuntimeStats { public static void RecordDisplayedAnswer(bool ok) { CompatibilityHost.Guard.Block<object>("answer displayed"); } }
    internal static class ReplyDeduplicationService { public static void RememberDelivered(string seller, string buyer, string answer) { CompatibilityHost.Guard.Block<object>("delivered reply"); } }
    internal sealed class AiEndpointConfig { public bool Enabled { get; set; } public bool SupportsVision { get; set; } public string VisionModel { get; set; } public string ApiKey { get; set; } public string BaseUrl { get; set; } }
    internal static class AiEndpointStore { public static IEnumerable<AiEndpointConfig> GetVisionEnabledEndpoints() { return CompatibilityHost.Guard.Block<IEnumerable<AiEndpointConfig>>("AI endpoint access"); } }
    internal static class ReplyModeService { public static bool IsLocalFirst(string seller) { return CompatibilityHost.Guard.Block<bool>("AI reply mode"); } }
}
