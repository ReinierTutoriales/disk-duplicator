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
/// Storage classification and soft backlog accounting only.
/// RepartoCopier intentionally keeps one physical write in flight per device,
/// matching the stable synchronous destination flow used by ExtremeCopy.
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
        if (device.IsNetwork) return new(StorageProfileKind.Network, 1, NetworkBacklog);
        if (device.MediaKind == StorageMediaKind.Rotational) return new(StorageProfileKind.Rotational, 1, RotationalBacklog);
        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var ssd = device.MediaKind == StorageMediaKind.SolidState && device.TrimEnabled == true;
            return new(ssd ? StorageProfileKind.UsbSsd : StorageProfileKind.UsbFlash, 1, ssd ? UsbSsdBacklog : UsbFlashBacklog);
        }
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.SataSsd, 1, SataSsdBacklog);
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Nvme, 1, NvmeBacklog);
        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog);
        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) || string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Virtual, 1, ConservativeBacklog);
        return new(StorageProfileKind.Conservative, 1, ConservativeBacklog);
    }
}
