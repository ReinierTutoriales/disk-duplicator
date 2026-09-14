namespace RepartoCopier.Core;

/// <summary>
/// Selects useful destination write concurrency from the adaptive scheduler's
/// exploration window and the current payload size. Hardware classes choose the
/// starting depth only; they do not cap future concurrency. There is no fixed
/// file-size or identity-confidence threshold: local payload size and the
/// adaptive physical scheduler are the practical bounds.
/// </summary>
public static class StorageWritePolicy
{
    // Scheduling granularity, not a queue-depth ceiling. Direct I/O raises this
    // automatically to the device alignment when necessary.
    public const int MinimumParallelSliceBytes = 4 * 1024;

    public static int LargeWriteQueueDepth(
        StorageDeviceInfo device,
        int schedulerExplorationDepth,
        int dataLength)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schedulerExplorationDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        if (dataLength < 2 * MinimumParallelSliceBytes ||
            schedulerExplorationDepth < 2 ||
            device.IsNetwork)
        {
            return 1;
        }

        var payloadDepth = Math.Max(1, dataLength / MinimumParallelSliceBytes);
        return Math.Max(1, Math.Min(payloadDepth, schedulerExplorationDepth));
    }
}
