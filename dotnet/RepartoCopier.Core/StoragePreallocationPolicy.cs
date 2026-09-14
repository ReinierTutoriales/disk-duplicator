using System.Collections.Concurrent;

namespace RepartoCopier.Core;

internal static class StoragePreallocationPolicy
{
    private static readonly ConcurrentDictionary<string, bool> VolumePolicy =
        new(StringComparer.OrdinalIgnoreCase);

    internal static long GetPreallocationSize(string path, long fileSize, long threshold)
    {
        if (fileSize < threshold || fileSize <= 0)
            return 0;

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            return 0;

        var allowed = VolumePolicy.GetOrAdd(root, static volumeRoot =>
        {
            try
            {
                var drive = new DriveInfo(volumeRoot);
                var isNetwork = StorageTopology.IsNetworkDestination(volumeRoot, drive.DriveType);
                if (isNetwork || !drive.IsReady)
                    return false;
                return StorageTopology.SupportsSafePreallocation(drive.DriveFormat, isNetwork: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        });

        return allowed ? fileSize : 0;
    }

}
