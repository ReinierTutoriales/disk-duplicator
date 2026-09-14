using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageIoProfileTests
{
    [TestMethod]
    public void PublicProfileShapeContainsOnlyProductionPolicyInputs()
    {
        var properties = typeof(StorageIoProfile)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(StorageIoProfile.DeviceBacklogTargetBytes),
                nameof(StorageIoProfile.Kind),
                nameof(StorageIoProfile.RecommendedQueueDepth),
            },
            properties);
    }

    [TestMethod]
    public void NetworkAndUsbFlashRemainConservative()
    {
        var network = Device("Network", StorageMediaKind.Unknown, isNetwork: true, preallocation: false);
        var usbFlash = Device("USB", StorageMediaKind.SolidState, isNetwork: false, preallocation: false, trim: false, removable: true);

        var networkProfile = StorageIoProfile.For(network);
        Assert.AreEqual(StorageProfileKind.Network, networkProfile.Kind);
        Assert.AreEqual(1, networkProfile.RecommendedQueueDepth);
        Assert.AreEqual(32L * 1024 * 1024, networkProfile.DeviceBacklogTargetBytes);

        var flashProfile = StorageIoProfile.For(usbFlash);
        Assert.AreEqual(StorageProfileKind.UsbFlash, flashProfile.Kind);
        Assert.AreEqual(1, flashProfile.RecommendedQueueDepth);
        Assert.AreEqual(16L * 1024 * 1024, flashProfile.DeviceBacklogTargetBytes);
    }

    [TestMethod]
    public void UsbSsdStartsAtQueueDepthOneWithBoundedBacklog()
    {
        var device = Device("USB", StorageMediaKind.SolidState, false, true, trim: true, removable: false);

        var profile = StorageIoProfile.For(device);

        Assert.AreEqual(StorageProfileKind.UsbSsd, profile.Kind);
        Assert.AreEqual(1, profile.RecommendedQueueDepth);
        Assert.AreEqual(32L * 1024 * 1024, profile.DeviceBacklogTargetBytes);
    }

    [TestMethod]
    public void SataAndNvmeUseConservativeStaticDefaults()
    {
        var sata = StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, false, true, trim: true));
        var nvme = StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, false, true, trim: true, alignmentOffset: 0));

        Assert.AreEqual(StorageProfileKind.SataSsd, sata.Kind);
        Assert.AreEqual(2, sata.RecommendedQueueDepth);
        Assert.AreEqual(64L * 1024 * 1024, sata.DeviceBacklogTargetBytes);

        Assert.AreEqual(StorageProfileKind.Nvme, nvme.Kind);
        Assert.AreEqual(2, nvme.RecommendedQueueDepth);
        Assert.AreEqual(128L * 1024 * 1024, nvme.DeviceBacklogTargetBytes);
    }

    [TestMethod]
    public void RotationalAndUnknownDevicesStayAtQueueDepthOne()
    {
        var hdd = StorageIoProfile.For(Device("SATA", StorageMediaKind.Rotational, false, true));
        var unknown = StorageIoProfile.For(Device("Unknown", StorageMediaKind.Unknown, false, false));

        Assert.AreEqual(1, hdd.RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Rotational, hdd.Kind);
        Assert.AreEqual(32L * 1024 * 1024, hdd.DeviceBacklogTargetBytes);
        Assert.AreEqual(1, unknown.RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Conservative, unknown.Kind);
        Assert.AreEqual(32L * 1024 * 1024, unknown.DeviceBacklogTargetBytes);
    }

    private static StorageDeviceInfo Device(
        string bus,
        StorageMediaKind media,
        bool isNetwork,
        bool preallocation,
        bool? trim = null,
        bool? removable = false,
        uint? alignmentOffset = null) =>
        new(
            @"C:\dest",
            @"C:\",
            isNetwork ? null : 1,
            isNetwork ? null : 1,
            bus,
            media,
            removable,
            512,
            4096,
            true,
            null,
            false,
            preallocation ? "NTFS" : "Unknown",
            isNetwork ? DriveType.Network : DriveType.Fixed,
            isNetwork,
            preallocation,
            trim,
            alignmentOffset);
}
