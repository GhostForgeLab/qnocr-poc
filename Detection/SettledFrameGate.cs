namespace QnocrPoc.Detection;

/// <summary>Latest-frame debounce; monotonic time supplied by caller. No frame history is retained.</summary>
public sealed class SettledFrameGate(TimeSpan settle, TimeSpan maximumWait)
{
    private string? _observed, _processed;
    private TimeSpan _changedAt, _pendingAt;
    private bool _pending;

    public bool Observe(string fingerprint, TimeSpan now)
    {
        if (_observed != fingerprint)
        {
            _observed = fingerprint;
            _changedAt = now;
            if (!_pending) _pendingAt = now;
            _pending = true;
        }
        // A transient change returned to the successfully processed frame.
        if (fingerprint == _processed) { _pending = false; return false; }
        return _pending && (now - _changedAt >= settle || now - _pendingAt >= maximumWait);
    }

    public void Complete(string fingerprint) { _processed = fingerprint; _pending = false; }
    public void Reset() { _observed = _processed = null; _pending = false; }
}
