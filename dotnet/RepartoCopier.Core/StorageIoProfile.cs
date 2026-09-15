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

        if (device.IsNetwork)
            return new(StorageProfileKind.Network, 1, NetworkBacklog);

        if (device.MediaKind == StorageMediaKind.Rotational)
            return new(StorageProfileKind.Rotational, 1, RotationalBacklog);

        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var looksLikeSsd =
                device.MediaKind == StorageMediaKind.SolidState &&
                device.TrimEnabled == true;

            if (!looksLikeSsd)
                return new(StorageProfileKind.UsbFlash, 1, UsbFlashBacklog);

            var exactPhysicalIdentity =
                StorageDeviceIdentity.ConfidenceFor(device) == DeviceIdentityConfidence.Exact;

            return new(
                StorageProfileKind.UsbSsd,
                exactPhysicalIdentity ? 4 : 1,
                exactPhysicalIdentity ? UsbSsdBacklog : RotationalBacklog);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.SataSsd, 8, SataSsdBacklog);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Nvme, 16, NvmeBacklog);
        }

        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog);

        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Virtual, 1, ConservativeBacklog);
        }

        return new(StorageProfileKind.Conservative, 1, ConservativeBacklog);
    }
}