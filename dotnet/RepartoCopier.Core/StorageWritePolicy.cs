namespace RepartoCopier.Core;

/// <summary>
/// Selects useful destination write concurrency from physical-device capability
/// and the current payload size. Depth is not globally capped at QD2; each slice
/// must remain large enough to avoid turning sequential throughput into tiny-I/O
/// overhead.
/// </summary>
public static class StorageWritePolicy
{
    public const int ParallelFileThresholdBytes = 8 * 1024 * 1024;
    public const int MinimumParallelSliceBytes = 1024 * 1024;

    public static int LargeWriteQueueDepth(
        StorageDeviceInfo device,
        int schedulerMaxOutstandingIo,
        long fileSize,
        int dataLength)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schedulerMaxOutstandingIo);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        if (fileSize < ParallelFileThresholdBytes ||
            dataLength < 2 * MinimumParallelSliceBytes ||
            schedulerMaxOutstandingIo < 2 ||
            device.IsNetwork ||
            device.SharesPhysicalDevice)
        {
            return 1;
        }

        var profile = StorageIoProfile.For(device);
        if (profile.Kind is not (StorageProfileKind.UsbSsd or StorageProfileKind.SataSsd or StorageProfileKind.Nvme))
            return 1;

        var payloadDepth = Math.Max(1, dataLength / MinimumParallelSliceBytes);
        return Math.Max(
            1,
            Math.Min(
                payloadDepth,
                Math.Min(profile.RecommendedQueueDepth, schedulerMaxOutstandingIo)));
    }
}
