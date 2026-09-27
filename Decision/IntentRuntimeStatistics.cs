using QnocrPoc.Models;

namespace QnocrPoc.Decision;

/// <summary>Counts completed per-message classifications; startup unavailability is reported separately.</summary>
public sealed class IntentRuntimeStatistics
{
    private readonly object _gate = new();
    private int _total, _jevSuccess, _jevFailure, _fallback, _startupUnavailable;
    private double _totalLatency;

    public void Observe(IntentProviderTrace trace)
    {
        lock (_gate)
        {
            if (trace.Event == "JEV_PROVIDER_UNAVAILABLE") _startupUnavailable++;
            if (trace.Event == "JEV_PROVIDER_FAILED") _jevFailure++;
            if (trace.Event != "INTENT_PROVIDER_RESULT") return;
            if (trace.LatencyMilliseconds is not { } latency || !double.IsFinite(latency) || latency < 0)
                throw new InvalidDataException("Completed intent classification needs valid latency.");
            _total++;
            if (trace.Provider == "JEV") _jevSuccess++;
            if (trace.FallbackUsed) _fallback++;
            _totalLatency += latency;
        }
    }

    public IntentRuntimeSnapshot Snapshot()
    {
        lock (_gate)
            return new IntentRuntimeSnapshot(_total, _jevSuccess, _jevFailure, _fallback,
                _total == 0 ? 0 : _totalLatency / _total, _startupUnavailable, DateTimeOffset.UtcNow);
    }
}
