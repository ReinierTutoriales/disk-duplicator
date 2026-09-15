namespace RepartoCopier.Core;

internal static class BranchReplayPlacement
{
    internal static string? ResolveSafeDirectory(
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var tempDevice = StorageTopology.InspectDestinations([tempRoot]).Destinations.Single();
            if (!IsSafePhysicalPlacement(tempDevice, source, destinations))
                return null;
            return Path.Combine(tempRoot, "RepartoCopier", "branch-replay");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsSafePhysicalPlacement(
        StorageDeviceInfo replayDevice,
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(replayDevice);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        if (!StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(replayDevice, out var replayNumber) ||
            !StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(source, out var sourceNumber) ||
            replayNumber == sourceNumber)
        {
            return false;
        }

        foreach (var destination in destinations)
        {
            if (!StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(destination, out var destinationNumber) ||
                replayNumber == destinationNumber)
            {
                return false;
            }
        }

        return true;
    }
}