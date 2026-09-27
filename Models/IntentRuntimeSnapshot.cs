namespace QnocrPoc.Models;

public sealed record IntentRuntimeSnapshot(int TotalIntentRequests, int JEVSuccess,
    int JEVFailure, int RuleFallbackCount, double AverageLatencyMilliseconds,
    int JevUnavailableAtStartup, DateTimeOffset ObservedAt);
