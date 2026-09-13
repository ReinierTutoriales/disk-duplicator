namespace RepartoCopier.Core;

/// <summary>
/// Selects the bounded buffered-write queue depth for one destination.
/// This is intentionally conservative: QD2 is enabled only for large local
/// SSD/NVMe writes on an exclusive physical device scheduler. QD4 remains a
/// benchmark-authorized future path and is not selected here.
/// </summary>
public static class StorageWritePolicy
{
    public const int ParallelFileThresholdBytes = 16 * 1024 * 1024;
    public const int MinimumParallelSliceBytes = 1024 * 1024;

    public static int BufferedLargeWriteQueueDepth(
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
        return profile.Kind is StorageProfileKind.SataSsd or StorageProfileKind.Nvme &&
               profile.RecommendedQueueDepth >= 2
            ? 2
            : 1;
    }
}
