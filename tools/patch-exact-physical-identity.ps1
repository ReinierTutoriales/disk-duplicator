$ErrorActionPreference = 'Stop'

$identityPath = 'dotnet/RepartoCopier.Core/DeviceIdentityConfidence.cs'
$schedulerPath = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
$testPath = 'dotnet/RepartoCopier.Core.Tests/StorageDeviceIdentityTests.cs'

$identity = @'
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
'@
Set-Content -Path $identityPath -Value $identity -NoNewline

$scheduler = Get-Content -Raw $schedulerPath
$old = @'
    internal static bool SharesPhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) =>
        left.PhysicalDeviceNumber is uint leftNumber &&
        right.PhysicalDeviceNumber is uint rightNumber &&
        leftNumber == rightNumber;
'@
$new = @'
    internal static bool SharesPhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) =>
        StorageDeviceIdentity.SamePhysicalDevice(left, right);
'@
$count = ([regex]::Matches($scheduler, [regex]::Escape($old))).Count
if ($count -ne 1) { throw "Expected one legacy physical-device comparison, found $count." }
$scheduler = $scheduler.Replace($old, $new)
Set-Content -Path $schedulerPath -Value $scheduler -NoNewline

$tests = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageDeviceIdentityTests
{
    [TestMethod]
    public void ExactDifferentDiskNumbersAreProvenDifferent()
    {
        var left = Exact(@"E:\\copy", 4);
        var right = Exact(@"F:\\copy", 7);

        Assert.IsTrue(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(left, right));
        Assert.IsFalse(StorageDeviceIdentity.SamePhysicalDevice(left, right));
    }

    [TestMethod]
    public void ExactSameDiskNumberIsSamePhysicalDeviceAndNotDifferent()
    {
        var left = Exact(@"E:\\copy", 4);
        var right = Exact(@"F:\\copy", 4);

        Assert.IsTrue(StorageDeviceIdentity.SamePhysicalDevice(left, right));
        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(left, right));
    }

    [TestMethod]
    public void PartialIdentityNeverProvesPhysicalDifference()
    {
        var exact = Exact(@"E:\\copy", 4);
        var partial = Partial(@"F:\\copy");

        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(exact, partial));
        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(partial, exact));
        Assert.IsFalse(StorageDeviceIdentity.SamePhysicalDevice(exact, partial));
    }

    [TestMethod]
    public void TwoPartialVolumeIdentitiesNeverPretendToBePhysicallyDifferent()
    {
        var left = Partial(@"E:\\copy");
        var right = Partial(@"F:\\copy");

        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(left, right));
        Assert.IsFalse(StorageDeviceIdentity.SamePhysicalDevice(left, right));
    }

    [TestMethod]
    public void UnknownIdentityNeverProvesPhysicalDifference()
    {
        var exact = Exact(@"E:\\copy", 4);
        var unknown = new StorageDeviceInfo(
            "relative",
            string.Empty,
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            "unsupported");

        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(exact, unknown));
        Assert.IsFalse(StorageDeviceIdentity.ProvenDifferentPhysicalDevices(unknown, exact));
        Assert.IsFalse(StorageDeviceIdentity.SamePhysicalDevice(exact, unknown));
    }

    [TestMethod]
    public void SchedulerCollisionCheckUsesExactIdentityContract()
    {
        var left = Exact(@"E:\\copy", 9);
        var right = Exact(@"F:\\copy", 9);
        var ambiguous = Partial(@"G:\\copy");

        Assert.IsTrue(DeviceSchedulerMap.SharesPhysicalDevice(left, right));
        Assert.IsFalse(DeviceSchedulerMap.SharesPhysicalDevice(left, ambiguous));
    }

    private static StorageDeviceInfo Exact(string root, uint disk) =>
        new(
            root,
            Path.GetPathRoot(root)!,
            disk,
            1,
            "USB",
            StorageMediaKind.SolidState,
            true,
            512,
            4096,
            true,
            null);

    private static StorageDeviceInfo Partial(string root) =>
        new(
            root,
            Path.GetPathRoot(root)!,
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            "physical identity unresolved");
}
'@
Set-Content -Path $testPath -Value $tests -NoNewline

if ((Get-Content -Raw $schedulerPath) -match 'left\.PhysicalDeviceNumber is uint leftNumber') {
    throw 'Legacy physical identity comparison remains in DeviceSchedulerMap.'
}
