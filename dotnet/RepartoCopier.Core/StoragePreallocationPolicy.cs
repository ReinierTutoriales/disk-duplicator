using System.Collections.Concurrent;
using System.Threading;

namespace RepartoCopier.Core;

internal static class StoragePreallocationPolicy
{
    internal const string DisablePreallocationEnvironmentVariable = "REPARTOCOPIER_DISABLE_PREALLOCATION";

    private static readonly ConcurrentDictionary<string, bool> VolumePolicy =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _diagnosticSwitchInitialized;
    private static int _preallocationDisabledForDiagnostics;

    internal static string DiagnosticState =>
        PreallocationDisabledForDiagnostics ? "disabled_by_env" : "enabled_default";

    internal static long GetPreallocationSize(string path, long fileSize)
    {
        if (fileSize <= 0)
            return 0;
        if (PreallocationDisabledForDiagnostics)
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

    internal static bool PreallocationDisabledForDiagnostics
    {
        get
        {
            if (Volatile.Read(ref _diagnosticSwitchInitialized) == 0)
            {
                var disabled = string.Equals(
                    Environment.GetEnvironmentVariable(DisablePreallocationEnvironmentVariable),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
                Volatile.Write(ref _preallocationDisabledForDiagnostics, disabled ? 1 : 0);
                Volatile.Write(ref _diagnosticSwitchInitialized, 1);
            }

            return Volatile.Read(ref _preallocationDisabledForDiagnostics) != 0;
        }
    }

    internal static void ResetDiagnosticsForTests()
    {
        Volatile.Write(ref _preallocationDisabledForDiagnostics, 0);
        Volatile.Write(ref _diagnosticSwitchInitialized, 0);
    }
}
