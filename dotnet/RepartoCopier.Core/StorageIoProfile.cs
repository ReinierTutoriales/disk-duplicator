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
    int MaximumQueueDepth,
    int RecommendedBlockSizeBytes,
    long DeviceBacklogTargetBytes,
    bool AllowDirectIo,
    bool AllowPreallocation,
    int SmallFileSyncConcurrency,
    bool BenchmarkCanRaiseQueueDepth)
{
    private const int MiB = 1024 * 1024;

    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.IsNetwork)
            return new(StorageProfileKind.Network, 1, 1, 2 * MiB, 32L * MiB, false, false, 1, false);

        if (device.MediaKind == StorageMediaKind.Rotational)
            return new(
                StorageProfileKind.Rotational,
                1,
                1,
                4 * MiB,
                32L * MiB,
                false,
                device.SupportsPreallocation,
                1,
                false);

        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var looksLikeSsd = device.MediaKind == StorageMediaKind.SolidState && device.TrimEnabled == true;
            return looksLikeSsd
                ? new(
                    StorageProfileKind.UsbSsd,
                    1,
                    2,
                    4 * MiB,
                    32L * MiB,
                    false,
                    device.SupportsPreallocation,
                    1,
                    true)
                : new(
                    StorageProfileKind.UsbFlash,
                    1,
                    1,
                    1024 * 1024,
                    16L * MiB,
                    false,
                    false,
                    1,
                    false);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                StorageProfileKind.SataSsd,
                2,
                2,
                8 * MiB,
                64L * MiB,
                false,
                device.SupportsPreallocation,
                1,
                false);
        }

        if (device.MediaKind == StorageMediaKind.SolidState &&
            string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
        {
            var directIoCandidate =
                device.SupportsPreallocation &&
                device.HasKnownSectorAlignment;
            return new(
                StorageProfileKind.Nvme,
                2,
                4,
                16 * MiB,
                128L * MiB,
                directIoCandidate,
                device.SupportsPreallocation,
                2,
                true);
        }

        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                StorageProfileKind.StorageSpaces,
                1,
                2,
                8 * MiB,
                64L * MiB,
                false,
                device.SupportsPreallocation,
                1,
                true);
        }

        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                StorageProfileKind.Virtual,
                1,
                1,
                4 * MiB,
                32L * MiB,
                false,
                false,
                1,
                false);
        }

        return new(
            StorageProfileKind.Conservative,
            1,
            1,
            4 * MiB,
            32L * MiB,
            false,
            device.SupportsPreallocation,
            1,
            false);
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
