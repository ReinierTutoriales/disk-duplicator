namespace RepartoCopier.Core;

public enum DeviceIdentityConfidence
{
    Unknown = 0,
    Partial = 1,
    Exact = 2,
}

public static class StorageDeviceIdentity
{
    public static DeviceIdentityConfidence ConfidenceFor(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.PhysicalDeviceNumber.HasValue)
            return DeviceIdentityConfidence.Exact;
        if (!string.IsNullOrWhiteSpace(device.VolumeRoot))
            return DeviceIdentityConfidence.Partial;
        return DeviceIdentityConfidence.Unknown;
    }

    internal static bool TryGetExactPhysicalDeviceNumber(StorageDeviceInfo device, out uint number)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (ConfidenceFor(device) == DeviceIdentityConfidence.Exact &&
            device.PhysicalDeviceNumber is uint physicalNumber)
        {
            number = physicalNumber;
            return true;
        }

        number = default;
        return false;
    }

    internal static bool SamePhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) =>
        TryGetExactPhysicalDeviceNumber(left, out var leftNumber) &&
        TryGetExactPhysicalDeviceNumber(right, out var rightNumber) &&
        leftNumber == rightNumber;

    internal static bool ProvenDifferentPhysicalDevices(StorageDeviceInfo left, StorageDeviceInfo right) =>
        TryGetExactPhysicalDeviceNumber(left, out var leftNumber) &&
        TryGetExactPhysicalDeviceNumber(right, out var rightNumber) &&
        leftNumber != rightNumber;
}