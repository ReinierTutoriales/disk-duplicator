$ErrorActionPreference = 'Stop'

# Run the already validated structural cleanup, then correct merge-only issues
# before compilation: preserve StorageIoProfile's exact public/behavior contract,
# collapse the duplicated CRC32C rate property created while removing aliases,
# and keep the whole-block architecture test focused on behavior rather than
# superseded parameter names.
& (Join-Path (Get-Location) 'tools/patch-unify-clean-main-v4.ps1')

$profile = @'
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

/// <summary>
/// Physical-device I/O policy consumed by the FAN-OUT scheduler.
/// InitialQueueDepth is only the starting point for adaptive physical-I/O
/// concurrency. It is deliberately not a maximum. DeviceBacklogTargetBytes is
/// a soft queue-pressure watermark only; it must not block the producer while
/// the global shared-buffer memory budget has capacity.
/// </summary>
public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int InitialQueueDepth,
    long DeviceBacklogTargetBytes)
{
    private const int MiB = 1024 * 1024;
    private const long ConservativeBacklog = 64L * MiB;
    private const long RotationalBacklog = 128L * MiB;
    private const long UsbFlashBacklog = 128L * MiB;
    private const long UsbSsdBacklog = 256L * MiB;
    private const long SataSsdBacklog = 256L * MiB;
    private const long NvmeBacklog = 512L * MiB;
    private const long NetworkBacklog = 32L * MiB;

    public static StorageIoProfile For(StorageDeviceInfo device)
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
'@
[System.IO.File]::WriteAllText(
    (Resolve-Path 'dotnet/RepartoCopier.Core/StorageIoProfile.cs'),
    $profile,
    [System.Text.UTF8Encoding]::new($false))

$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$t = [System.IO.File]::ReadAllText((Resolve-Path $telemetryPath))
$rateLine = '    public double VerifyCrc32CBytesPerSecond => Rate(VerifyCrc32CBytes, VerifyCrc32CTime);'
$first = $t.IndexOf($rateLine)
if ($first -lt 0) { throw 'Canonical CRC32C rate property missing.' }
$second = $t.IndexOf($rateLine, $first + $rateLine.Length)
if ($second -lt 0) { throw 'Expected duplicate CRC32C rate property was not produced.' }
$t = $t.Remove($second, $rateLine.Length)
[System.IO.File]::WriteAllText((Resolve-Path $telemetryPath), $t, [System.Text.UTF8Encoding]::new($false))

# The whole-block contract must reject depth/slicing controls without coupling the
# test to incidental parameter names changed by the unification.
$architectureTestPath = 'dotnet/RepartoCopier.Core.Tests/SequentialBlockWriteArchitectureTests.cs'
$a = [System.IO.File]::ReadAllText((Resolve-Path $architectureTestPath))
$old = @'
        CollectionAssert.AreEqual(
            new[] { "handle", "data", "baseOffset", "scheduler", "token", "requiredAlignment" },
            names,
            "El coordinador no debe recuperar controles de depth/slicing dentro de un bloque FAN-OUT.");
        Assert.AreEqual(typeof(Task<int>), write.ReturnType);
'@
$new = @'
        Assert.AreEqual(6, names.Length);
        CollectionAssert.DoesNotContain(names, "depth");
        CollectionAssert.DoesNotContain(names, "queueDepth");
        CollectionAssert.DoesNotContain(names, "sliceSize");
        CollectionAssert.DoesNotContain(names, "chunkSize");
        CollectionAssert.DoesNotContain(names, "maxInFlight");
        Assert.AreEqual(typeof(Task<int>), write.ReturnType);
'@
if (-not $a.Contains($old)) { throw 'Whole-block architecture test anchor missing.' }
$a = $a.Replace($old, $new)
[System.IO.File]::WriteAllText((Resolve-Path $architectureTestPath), $a, [System.Text.UTF8Encoding]::new($false))

# The unification must not change the established storage-profile behavior contract.
if (Test-Path 'dotnet/RepartoCopier.Core/FanoutPerformancePolicy.cs') {
    throw 'FanoutPerformancePolicy should have been removed by the base cleanup.'
}

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Behavior-preserving storage merge, CRC32C cleanup, and whole-block contract update applied.'