using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageTopologyTests
{
    [TestMethod]
    public void BuildSnapshotMarksDestinationsThatShareAPhysicalDisk()
    {
        var devices = new[]
        {
            Device(@"E:\\copy", 4, 1),
            Device(@"F:\\copy", 4, 2),
            Device(@"G:\\copy", 7, 1),
        };

        var snapshot = StorageTopology.BuildSnapshot(devices);

        Assert.HasCount(1, snapshot.SharedPhysicalDevices);
        Assert.AreEqual((uint)4, snapshot.SharedPhysicalDevices[0].PhysicalDeviceNumber);
        Assert.HasCount(2, snapshot.SharedPhysicalDevices[0].DestinationRoots);
        Assert.IsTrue(snapshot.Destinations[0].SharesPhysicalDevice);
        Assert.IsTrue(snapshot.Destinations[1].SharesPhysicalDevice);
        Assert.IsFalse(snapshot.Destinations[2].SharesPhysicalDevice);
    }

    [TestMethod]
    public void BuildSnapshotDoesNotGroupUnknownDevices()
    {
        var devices = new[]
        {
            Unknown(@"\\server\share-a"),
            Unknown(@"\\server\share-b"),
        };

        var snapshot = StorageTopology.BuildSnapshot(devices);

        Assert.IsEmpty(snapshot.SharedPhysicalDevices);
        Assert.IsTrue(snapshot.Destinations.All(item => !item.SharesPhysicalDevice));
    }

    [TestMethod]
    public void IdentityConfidenceIsExactWhenPhysicalDiskIsKnown()
    {
        var device = Device(@"E:\\copy", 4, 1);

        Assert.AreEqual(
            DeviceIdentityConfidence.Exact,
            StorageDeviceIdentity.ConfidenceFor(device));
    }

    [TestMethod]
    public void IdentityConfidenceIsPartialWhenOnlyVolumeIdentityIsKnown()
    {
        var device = new StorageDeviceInfo(
            @"E:\\copy",
            @"E:\",
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            "probe failed");

        Assert.AreEqual(
            DeviceIdentityConfidence.Partial,
            StorageDeviceIdentity.ConfidenceFor(device));
    }

    [TestMethod]
    public void IdentityConfidenceIsUnknownWithoutPhysicalOrVolumeIdentity()
    {
        var device = new StorageDeviceInfo(
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

        Assert.AreEqual(
            DeviceIdentityConfidence.Unknown,
            StorageDeviceIdentity.ConfidenceFor(device));
    }

    [TestMethod]
    public void SingleDiskExtentFallbackRecoversPhysicalIdentityWithoutAThrottlePolicy()
    {
        var descriptor = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0, 4), 1);
        var offset = IntPtr.Size == 8 ? 8 : 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(offset, 4), 27);

        Assert.IsTrue(StorageTopology.TryParseSingleDiskExtent(descriptor, IntPtr.Size, out var disk));
        Assert.AreEqual((uint)27, disk);
    }

    [TestMethod]
    public void MultiDiskExtentDoesNotPretendToHaveOnePhysicalIdentity()
    {
        var descriptor = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0, 4), 2);

        Assert.IsFalse(StorageTopology.TryParseSingleDiskExtent(descriptor, IntPtr.Size, out _));
    }

    [TestMethod]
    public void InspectDestinationsReturnsOneEntryPerDestination()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));

        var snapshot = StorageTopology.InspectDestinations(new[] { root! });

        Assert.HasCount(1, snapshot.Destinations);
        var device = snapshot.Destinations[0];
        Assert.AreEqual(Path.GetFullPath(root!), device.DestinationRoot);
        Assert.IsFalse(string.IsNullOrWhiteSpace(device.VolumeRoot));
    }

    private static StorageDeviceInfo Device(string root, uint disk, uint partition) =>
        new(
            root,
            Path.GetPathRoot(root)!,
            disk,
            partition,
            "USB",
            StorageMediaKind.SolidState,
            true,
            512,
            4096,
            true,
            null);

    private static StorageDeviceInfo Unknown(string root) =>
        new(
            root,
            Path.GetPathRoot(root) ?? string.Empty,
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            "unsupported");
}
