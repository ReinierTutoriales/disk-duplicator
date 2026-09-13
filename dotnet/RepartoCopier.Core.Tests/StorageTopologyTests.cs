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
