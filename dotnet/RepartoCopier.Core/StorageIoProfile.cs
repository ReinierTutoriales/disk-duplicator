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
/// Physical-device I/O limits consumed by the FAN-OUT scheduler.
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

public sealed record StorageDeviceProfile(
    StorageDeviceInfo Device,
    StorageIoProfile Io)
{
    public static StorageDeviceProfile Create(StorageDeviceInfo device) =>
        new(device, StorageIoProfile.For(device));

    public string DeviceId => Device.PhysicalDeviceId;
}
