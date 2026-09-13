using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

public sealed class StorageTopologyTests
{
    [Fact]
    public void BuildSnapshot_marks_destinations_that_share_a_physical_disk()
    {
        var devices = new[]
        {
            Device(@"E:\\copy", 4, 1),
            Device(@"F:\\copy", 4, 2),
            Device(@"G:\\copy", 7, 1),
        };

        var snapshot = StorageTopology.BuildSnapshot(devices);

        Assert.Single(snapshot.SharedPhysicalDevices);
        Assert.Equal((uint)4, snapshot.SharedPhysicalDevices[0].PhysicalDeviceNumber);
        Assert.Equal(2, snapshot.SharedPhysicalDevices[0].DestinationRoots.Count);
        Assert.True(snapshot.Destinations[0].SharesPhysicalDevice);
        Assert.True(snapshot.Destinations[1].SharesPhysicalDevice);
        Assert.False(snapshot.Destinations[2].SharesPhysicalDevice);
    }

    [Fact]
    public void BuildSnapshot_does_not_group_unknown_devices()
    {
        var devices = new[]
        {
            Unknown(@"\\server\share-a"),
            Unknown(@"\\server\share-b"),
        };

        var snapshot = StorageTopology.BuildSnapshot(devices);

        Assert.Empty(snapshot.SharedPhysicalDevices);
        Assert.All(snapshot.Destinations, item => Assert.False(item.SharesPhysicalDevice));
    }

    [Fact]
    public void InspectDestinations_returns_one_entry_per_destination()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(root));

        var snapshot = StorageTopology.InspectDestinations(new[] { root! });

        var device = Assert.Single(snapshot.Destinations);
        Assert.Equal(Path.GetFullPath(root!), device.DestinationRoot);
        Assert.False(string.IsNullOrWhiteSpace(device.VolumeRoot));
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
