namespace RepartoCopier.Core;

public enum StorageProfileKind
{
    Conservative,
    Network,
    Rotational,
    UsbFlash,
    UsbSsd,
    SataSsd,
    Nvme,
    Virtual,
    StorageSpaces,
}

/// <summary>
/// Physical-device I/O policy consumed by the FAN-OUT scheduler.
/// RecommendedQueueDepth is a hard physical-I/O concurrency limit.
/// DeviceBacklogTargetBytes is a soft queue-pressure watermark only; it must not
/// block the producer while the global shared-buffer memory budget has capacity.
/// Policy lives in <see cref="FanoutPerformancePolicy"/> so topology description
/// and performance tuning remain separate concerns.
/// </summary>
public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int RecommendedQueueDepth,
    long DeviceBacklogTargetBytes)
{
    public static StorageIoProfile For(StorageDeviceInfo device) =>
        FanoutPerformancePolicy.For(device);
}
