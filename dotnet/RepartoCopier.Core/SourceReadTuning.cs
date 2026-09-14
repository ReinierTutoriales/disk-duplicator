namespace RepartoCopier.Core;

internal readonly record struct SourceReadTuning(
    int BlockSizeBytes,
    int PrefetchPhysicalCapacity,
    int HashPipelineCapacity,
    int InitialPrefetch,
    int MaximumPrefetch)
{
    private const int MiB = 1024 * 1024;

    internal static SourceReadTuning For(StorageDeviceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var profile = StorageIoProfile.For(source);
        var exactIdentity =
            StorageDeviceIdentity.ConfidenceFor(source) == DeviceIdentityConfidence.Exact;
        var fastLocalSsd =
            !source.IsNetwork &&
            exactIdentity &&
            profile.Kind is StorageProfileKind.UsbSsd or StorageProfileKind.SataSsd or StorageProfileKind.Nvme;

        return fastLocalSsd
            ? new SourceReadTuning(
                BlockSizeBytes: 32 * MiB,
                PrefetchPhysicalCapacity: 8,
                HashPipelineCapacity: 4,
                InitialPrefetch: 4,
                MaximumPrefetch: 8)
            : new SourceReadTuning(
                BlockSizeBytes: 16 * MiB,
                PrefetchPhysicalCapacity: 4,
                HashPipelineCapacity: 2,
                InitialPrefetch: 2,
                MaximumPrefetch: 4);
    }
}
