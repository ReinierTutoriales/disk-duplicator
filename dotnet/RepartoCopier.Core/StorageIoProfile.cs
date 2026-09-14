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
/// InitialQueueDepth is only the starting point for adaptive physical-I/O
/// concurrency. It is deliberately not a maximum. DeviceBacklogTargetBytes is
/// a soft queue-pressure watermark only; it must not block the producer while
/// the global shared-buffer memory budget has capacity.
/// </summary>
public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int InitialQueueDepth,
    long DeviceBacklogTargetBytes)
{
    public static StorageIoProfile For(StorageDeviceInfo device) =>
        FanoutPerformancePolicy.For(device);
}
