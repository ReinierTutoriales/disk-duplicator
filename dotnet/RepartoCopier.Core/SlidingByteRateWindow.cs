using System.Diagnostics;

namespace RepartoCopier.Core;

/// <summary>
/// Thread-safe sliding throughput window. Stores cumulative byte samples and
/// derives rates from actual recent progress, not an EWMA or instantaneous burst.
/// One implementation is shared by global diagnostics and each destination.
/// </summary>
internal sealed class SlidingByteRateWindow
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(12);
    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private long _totalBytes;

    internal void Record(int bytes) => Record(bytes, Stopwatch.GetTimestamp());

    internal void Record(int bytes, long timestamp)
    {
        if (bytes <= 0)
            return;
        lock (_gate)
        {
            _totalBytes = checked(_totalBytes + bytes);
            _samples.Enqueue(new Sample(timestamp, _totalBytes));
            TrimLocked(timestamp);
        }
    }

    internal SlidingByteRateSnapshot Snapshot() => Snapshot(Stopwatch.GetTimestamp());

    internal SlidingByteRateSnapshot Snapshot(long now)
    {
        lock (_gate)
        {
            TrimLocked(now);
            return new SlidingByteRateSnapshot(
                RateLocked(now, TimeSpan.FromSeconds(5)),
                RateLocked(now, TimeSpan.FromSeconds(10)));
        }
    }

    private double RateLocked(long now, TimeSpan window)
    {
        if (_totalBytes <= 0 || _samples.Count == 0)
            return 0;

        var cutoff = now - (long)(window.TotalSeconds * Stopwatch.Frequency);
        var baseline = _samples.Peek();
        foreach (var sample in _samples)
        {
            baseline = sample;
            if (sample.Timestamp >= cutoff)
                break;
        }

        var elapsed = Stopwatch.GetElapsedTime(Math.Max(baseline.Timestamp, cutoff), now).TotalSeconds;
        if (elapsed <= 0)
            return 0;
        var bytes = Math.Max(0L, _totalBytes - baseline.TotalBytes);
        return bytes / elapsed;
    }

    private void TrimLocked(long now)
    {
        var cutoff = now - (long)(Retention.TotalSeconds * Stopwatch.Frequency);
        while (_samples.Count > 1 && _samples.Peek().Timestamp < cutoff)
            _samples.Dequeue();
    }

    private readonly record struct Sample(long Timestamp, long TotalBytes);
}

internal readonly record struct SlidingByteRateSnapshot(
    double FiveSecondsBytesPerSecond,
    double TenSecondsBytesPerSecond);