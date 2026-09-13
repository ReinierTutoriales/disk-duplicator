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
}
