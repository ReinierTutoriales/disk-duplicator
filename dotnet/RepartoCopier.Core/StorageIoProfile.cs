namespace RepartoCopier.Core;

public enum StorageProfileKind
{
    Conservative,
    Network,
    Rotational,
    Usb,
    SataSsd,
    Nvme,
}

public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int RecommendedQueueDepth,
    bool AllowDirectIo,
    bool AllowPreallocation,
    int SmallFileSyncConcurrency)
{
    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsNetwork)
            return new(StorageProfileKind.Network, 1, false, false, 1);

        if (device.MediaKind == StorageMediaKind.Rotational)
            return new(StorageProfileKind.Rotational, 1, false, device.SupportsPreallocation, 1);

        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Usb, 1, false, device.SupportsPreallocation, 1);

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.SataSsd, 2, false, device.SupportsPreallocation, 1);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Nvme, 4, true, device.SupportsPreallocation, 2);
        }

        return new(StorageProfileKind.Conservative, 1, false, device.SupportsPreallocation, 1);
    }
}
