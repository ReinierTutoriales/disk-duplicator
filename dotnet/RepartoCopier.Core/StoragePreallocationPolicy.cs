using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace RepartoCopier.Core;

internal static class StoragePreallocationPolicy
{
    // Keyed by the folder that holds the file: every part of one destination lives in the same state folder,
    // so the volume lookup runs once per destination rather than once per file.
    private static readonly ConcurrentDictionary<string, bool> FolderPolicy =
        new(StringComparer.OrdinalIgnoreCase);

    internal static long GetPreallocationSize(string path, long fileSize)
    {
        if (fileSize <= 0)
            return 0;

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(folder))
            return 0;

        return FolderPolicy.GetOrAdd(folder, static directory => Allowed(directory)) ? fileSize : 0;
    }

    // Decided by the volume that really holds the folder: a volume mounted at C:\mnt\usb is judged by its own
    // file system, not by C:'s.
    private static bool Allowed(string directory)
    {
        try
        {
            var root = VolumeRootOf(directory) ?? Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            if (root.Length <= 3)
            {
                var drive = new DriveInfo(root);
                var isNetwork = StorageTopology.IsNetworkDestination(root, drive.DriveType);
                return !isNetwork && drive.IsReady &&
                       StorageTopology.SupportsSafePreallocation(drive.DriveFormat, isNetwork: false);
            }

            if (NativeMethods.GetDriveTypeW(root) == DriveRemote)
                return false;
            var fileSystem = new StringBuilder(64);
            return NativeMethods.GetVolumeInformationW(root, null, 0, out _, out _, out _, fileSystem,
                       (uint)fileSystem.Capacity) &&
                   StorageTopology.SupportsSafePreallocation(fileSystem.ToString(), isNetwork: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private const uint DriveRemote = 4;

    private static string? VolumeRootOf(string directory)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var mount = new StringBuilder(32768);
        return NativeMethods.GetVolumePathNameW(directory, mount, (uint)mount.Capacity) ? mount.ToString() : null;
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // StringBuilder marshaling keeps these one-shot volume queries readable.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            uint volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            uint fileSystemNameSize);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint GetDriveTypeW(string rootPathName);
#pragma warning restore SYSLIB1054
    }
}
