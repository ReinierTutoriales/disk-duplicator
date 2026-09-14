namespace RepartoCopier.Core;

/// <summary>
/// Performance policy for one physical FAN-OUT destination branch.
///
/// The copy engine reads each source block once and shares the same buffer with
/// every destination worker. These backlog targets therefore describe how far a
/// branch may run behind before it applies backpressure; they do not allocate a
/// private payload copy per destination. The global AdaptiveByteBudget remains
/// the hard memory ceiling.
///
/// Targets are intentionally expressed as multiples of the 32 MiB large-file
/// block used by CopyEngine. Because SharedBlock payload is reference-counted,
/// deeper independent branch windows improve tolerance to transient device/USB
/// latency without multiplying payload memory by destination count.
/// </summary>
internal static class FanoutPerformancePolicy
{
    private const int MiB = 1024 * 1024;

    private const long ConservativeBacklog = 64L * MiB;  // 2 x 32 MiB
    private const long RotationalBacklog = 128L * MiB;   // 4 x 32 MiB
    private const long UsbFlashBacklog = 128L * MiB;     // 4 x 32 MiB
    private const long UsbSsdBacklog = 256L * MiB;       // 8 x 32 MiB
    private const long SataSsdBacklog = 256L * MiB;      // 8 x 32 MiB
    private const long NvmeBacklog = 512L * MiB;         // 16 x 32 MiB
    private const long NetworkBacklog = 32L * MiB;

    internal static StorageIoProfile For(StorageDeviceInfo device)
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

            // External SSD/UASP enclosures commonly report the media as removable.
            // Removable is therefore not a useful reason to disable overlapped QD2.
            // Exact physical identity is retained as the safety gate so partitions
            // that actually share one device still share a single scheduler.
            var exactPhysicalIdentity =
                StorageDeviceIdentity.ConfidenceFor(device) == DeviceIdentityConfidence.Exact;

            return new(
                StorageProfileKind.UsbSsd,
                exactPhysicalIdentity ? 2 : 1,
                exactPhysicalIdentity ? UsbSsdBacklog : RotationalBacklog);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.SataSsd, 2, SataSsdBacklog);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return new(StorageProfileKind.Nvme, 2, NvmeBacklog);
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
