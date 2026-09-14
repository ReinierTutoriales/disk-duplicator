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