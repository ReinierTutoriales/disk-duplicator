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

public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int RecommendedQueueDepth,
    long DeviceBacklogTargetBytes)
{
    private const int MiB = 1024 * 1024;

    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsNetwork)
            return new(StorageProfileKind.Network, 1, 32L * MiB);

        if (device.MediaKind == StorageMediaKind.Rotational)
            return new(StorageProfileKind.Rotational, 1, 32L * MiB);

        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var looksLikeSsd = device.MediaKind == StorageMediaKind.SolidState && device.TrimEnabled == true;
            return looksLikeSsd
                ? new(StorageProfileKind.UsbSsd, 1, 32L * MiB)
                : new(StorageProfileKind.UsbFlash, 1, 16L * MiB);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.SataSsd, 2, 64L * MiB);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Nvme, 2, 128L * MiB);
        }

        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.StorageSpaces, 1, 64L * MiB);

        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Virtual, 1, 32L * MiB);
        }

        return new(StorageProfileKind.Conservative, 1, 32L * MiB);
    }
}

public sealed record StorageDeviceProfile(
    StorageDeviceInfo Device,
    StorageIoProfile Io)
{
    public static StorageDeviceProfile Create(StorageDeviceInfo device) =>
        new(device, StorageIoProfile.For(device));

    public string DeviceId => Device.PhysicalDeviceId;
}
