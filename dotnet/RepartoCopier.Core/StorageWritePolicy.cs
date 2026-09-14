namespace RepartoCopier.Core;

/// <summary>
/// Selects useful destination write concurrency from the adaptive scheduler's
/// exploration window and the current payload size. Hardware classes choose the
/// starting depth only; they do not cap future concurrency. The payload itself
/// is the final practical bound: we never create more slices than it can contain.
/// </summary>
public static class StorageWritePolicy
{
    public const int ParallelFileThresholdBytes = 8 * 1024 * 1024;

    // Scheduling granularity, not a queue-depth ceiling. Direct I/O raises this
    // automatically to the device alignment when necessary.
    public const int MinimumParallelSliceBytes = 4 * 1024;

    public static int LargeWriteQueueDepth(
        StorageDeviceInfo device,
        int schedulerExplorationDepth,
        long fileSize,
        int dataLength)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schedulerExplorationDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        if (fileSize < ParallelFileThresholdBytes ||
            dataLength < 2 * MinimumParallelSliceBytes ||
            schedulerExplorationDepth < 2 ||
            device.IsNetwork ||
            StorageDeviceIdentity.ConfidenceFor(device) != DeviceIdentityConfidence.Exact)
        {
            return 1;
        }

        var payloadDepth = Math.Max(1, dataLength / MinimumParallelSliceBytes);
        return Math.Max(1, Math.Min(payloadDepth, schedulerExplorationDepth));
    }
}
