namespace Wicked.Core.Downloads;

/// <summary>
/// A shared bandwidth cap. Callers take tokens (bytes) before using them and wait out the debt, so any chunk size works
/// and the limit can change while transfers run: the next chunk sees the new rate. Null means unlimited.
/// </summary>
internal sealed class TokenBucket(TimeProvider time)
{
    private readonly Lock _gate = new();
    private long? _rate;
    private double _tokens;
    private long _stamp = time.GetTimestamp();

    public long? BytesPerSecond
    {
        get { lock (_gate) return _rate; }
        set
        {
            lock (_gate)
            {
                Refill();
                _rate = value is > 0 ? value : null;
                if (_rate is null) _tokens = 0;
            }
        }
    }

    public async ValueTask TakeAsync(int bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_rate is not { } rate) return;
            Refill();
            _tokens -= bytes;
            // Going into debt means the caller sleeps until the rate has paid it off.
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct).ConfigureAwait(false);
    }

    private void Refill()
    {
        var now = time.GetTimestamp();
        var elapsed = time.GetElapsedTime(_stamp, now).TotalSeconds;
        _stamp = now;
        if (_rate is not { } rate) return;
        // Credit never banks more than a quarter second, so an idle job cannot later burst past the cap.
        _tokens = Math.Min(_tokens + elapsed * rate, rate * 0.25);
    }
}
