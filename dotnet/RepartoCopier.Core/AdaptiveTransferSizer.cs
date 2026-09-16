namespace RepartoCopier.Core;

internal readonly record struct TransferDeviceSignal(
    int CurrentQueueDepth,
    int BestQueueDepth,
    double BestThroughputBytesPerSecond,
    double BestAverageLatencyMilliseconds);

/// <summary>
/// Chooses the source transfer size from current memory headroom, destination
/// concurrency and measured physical I/O. There are no file-size bands and no
/// fixed large-block target. Before physical feedback exists, memory headroom is
/// distributed across the blocks needed to sustain current QD/prefetch demand.
/// Once throughput/latency feedback exists, Little's-law bytes-per-operation is
/// used as an evidence-based target, still bounded by current memory pressure.
/// </summary>
internal static class AdaptiveTransferSizer
{
    internal static int Select(
        long fileSize,
        int activeDestinations,
        int requiredAlignment,
        long bufferTargetBytes,
        long bufferUsedBytes,
        int currentPrefetchLimit,
        IReadOnlyList<TransferDeviceSignal> devices)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activeDestinations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredAlignment);
        if ((requiredAlignment & (requiredAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(requiredAlignment));
        ArgumentOutOfRangeException.ThrowIfNegative(bufferTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferUsedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentPrefetchLimit);
        ArgumentNullException.ThrowIfNull(devices);

        var headroom = Math.Max(
            (long)requiredAlignment,
            Math.Max(0L, bufferTargetBytes - bufferUsedBytes));
        // A source block exists once regardless of destination count. Branch-local
        // staging detaches lagging consumers, so one destination's QD must not shrink
        // the global source transfer size for every other destination.
        var residentBlocks = checked(Math.Max(1, currentPrefetchLimit) + 1);
        var memoryBound = Math.Max(
            (long)requiredAlignment,
            headroom / Math.Max(1, residentBlocks));

        double largestMeasuredBytesPerOperation = 0;
        foreach (var device in devices)
        {
            if (device.BestThroughputBytesPerSecond <= 0 ||
                device.BestAverageLatencyMilliseconds <= 0)
            {
                continue;
            }

            var qd = Math.Max(1, device.BestQueueDepth);
            var bytes = device.BestThroughputBytesPerSecond *
                (device.BestAverageLatencyMilliseconds / 1000.0) / qd;
            if (!double.IsFinite(bytes) || bytes <= 0)
                continue;
            largestMeasuredBytesPerOperation = Math.Max(largestMeasuredBytesPerOperation, bytes);
        }

        long candidate = memoryBound;
        if (largestMeasuredBytesPerOperation > 0)
        {
            var measured = (long)Math.Max(requiredAlignment, largestMeasuredBytesPerOperation);
            candidate = Math.Min(memoryBound, measured);
        }

        var maxIntTransfer = (long)int.MaxValue - requiredAlignment;
        candidate = Math.Min(candidate, Math.Max(requiredAlignment, maxIntTransfer));
        if (fileSize > 0)
            candidate = Math.Min(candidate, Math.Max((long)requiredAlignment, fileSize));

        var aligned = candidate - candidate % requiredAlignment;
        if (aligned < requiredAlignment)
            aligned = requiredAlignment;
        return checked((int)aligned);
    }
}
