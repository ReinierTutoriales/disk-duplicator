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
/// Static storage classification used to select a fixed per-device queue-depth ceiling
/// and backlog budget. Queue depth is chosen once from hardware identity; there is no
/// runtime probing, throughput exploration, or adaptive queue-depth tuning.
/// </summary>
public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int InitialQueueDepth,
    long DeviceBacklogTargetBytes)
{
    private const int MiB = 1024 * 1024;
    private const long ConservativeBacklog = 64L * MiB;
    private const long RotationalBacklog = 128L * MiB;
    private const long UsbFlashBacklog = 128L * MiB;
    private const long UsbSsdBacklog = 256L * MiB;
    private const long SataSsdBacklog = 256L * MiB;
    private const long NvmeBacklog = 512L * MiB;
    private const long NetworkBacklog = 32L * MiB;

    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsNetwork) return new(StorageProfileKind.Network, 2, NetworkBacklog);
        if (device.MediaKind == StorageMediaKind.Rotational) return new(StorageProfileKind.Rotational, 2, RotationalBacklog);
        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var ssd = device.MediaKind == StorageMediaKind.SolidState && device.TrimEnabled == true;
            return new(ssd ? StorageProfileKind.UsbSsd : StorageProfileKind.UsbFlash, ssd ? 4 : 1, ssd ? UsbSsdBacklog : UsbFlashBacklog);
        }
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.SataSsd, 4, SataSsdBacklog);
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Nvme, 8, NvmeBacklog);
        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog);
        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) || string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Virtual, 1, ConservativeBacklog);
        return new(StorageProfileKind.Conservative, 1, ConservativeBacklog);
    }
}
