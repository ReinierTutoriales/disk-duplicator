namespace RepartoCopier.Core;

/// <summary>
/// Global bounded budget for per-destination FAN-OUT spill. Reservations are
/// non-blocking by design: a lagging destination must never stall the producer.
/// </summary>
internal sealed class FanoutSpillBudget
{
    internal const long DefaultCapacityBytes = 512L * 1024 * 1024;
    internal const long MinimumDestinationBytes = 16L * 1024 * 1024;

    private long _usedBytes;
    private long _peakUsedBytes;

    internal FanoutSpillBudget(long capacityBytes = DefaultCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    internal long CapacityBytes { get; }
    internal long UsedBytes => Interlocked.Read(ref _usedBytes);
    internal long PeakUsedBytes => Interlocked.Read(ref _peakUsedBytes);

    internal long DestinationCeilingForCurrentActiveCount(
        Func<int> activeDestinationCount,
        long backlogTargetBytes)
    {
        ArgumentNullException.ThrowIfNull(activeDestinationCount);
        return DestinationCeiling(Math.Max(1, activeDestinationCount()), backlogTargetBytes);
    }

    internal long DestinationCeiling(int activeDestinations, long backlogTargetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeDestinations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);
        var fairShare = CapacityBytes / activeDestinations;
        return Math.Min(backlogTargetBytes, Math.Max(MinimumDestinationBytes, fairShare));
    }

    internal bool TryReserve(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        while (true)
        {
            var current = Interlocked.Read(ref _usedBytes);
            var next = checked(current + bytes);
            if (next > CapacityBytes)
                return false;
            if (Interlocked.CompareExchange(ref _usedBytes, next, current) != current)
                continue;
            UpdateMax(ref _peakUsedBytes, next);
            return true;
        }
    }

    internal void Release(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        while (true)
        {
            var current = Interlocked.Read(ref _usedBytes);
            if (current < bytes)
                throw new InvalidOperationException("El spill global intentó liberar más bytes de los reservados.");
            if (Interlocked.CompareExchange(ref _usedBytes, current - bytes, current) == current)
                return;
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
