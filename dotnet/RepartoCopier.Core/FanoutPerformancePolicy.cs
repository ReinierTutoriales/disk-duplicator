namespace RepartoCopier.Core;

/// <summary>
/// Performance policy for one physical FAN-OUT destination branch.
///
/// The copy engine reads each source block once and shares the same buffer with
/// every destination worker. Backlog targets are soft pressure watermarks used
/// to classify how far a branch has fallen behind; they are not producer gates.
/// A slow branch may therefore exceed its target while global shared-buffer
/// memory remains available. AdaptiveByteBudget is the hard payload-memory
/// ceiling; DeviceScheduler queue depth remains the hard physical-I/O limit.
///
/// Queue-depth ceilings are intentionally hardware-class aware rather than
/// capped globally at QD2. The write policy further bounds useful depth by the
/// current payload size, so these are capabilities, not forced concurrency.
/// </summary>
internal static class FanoutPerformancePolicy
{
    private const int MiB = 1024 * 1024;

    private const long ConservativeBacklog = 64L * MiB;
    private const long RotationalBacklog = 128L * MiB;
    private const long UsbFlashBacklog = 128L * MiB;
    private const long UsbSsdBacklog = 256L * MiB;
    private const long SataSsdBacklog = 256L * MiB;
    private const long NvmeBacklog = 512L * MiB;
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
