using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageIoProfileTests
{
    [TestMethod]
    public void NetworkAndUsbRemainConservative()
    {
        var network = Device("Network", StorageMediaKind.Unknown, isNetwork: true, preallocation: false);
        var usb = Device("USB", StorageMediaKind.SolidState, isNetwork: false, preallocation: false);

        Assert.AreEqual(1, StorageIoProfile.For(network).RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Network, StorageIoProfile.For(network).Kind);
        Assert.IsFalse(StorageIoProfile.For(network).AllowDirectIo);

        Assert.AreEqual(1, StorageIoProfile.For(usb).RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Usb, StorageIoProfile.For(usb).Kind);
        Assert.IsFalse(StorageIoProfile.For(usb).AllowDirectIo);
    }

    [TestMethod]
    public void SataAndNvmeUseFixedStaticQueueDepthProfiles()
    {
        var sata = StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, false, true));
        var nvme = StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, false, true));

        Assert.AreEqual(StorageProfileKind.SataSsd, sata.Kind);
        Assert.AreEqual(2, sata.RecommendedQueueDepth);
        Assert.IsFalse(sata.AllowDirectIo);
        Assert.IsTrue(sata.AllowPreallocation);

        Assert.AreEqual(StorageProfileKind.Nvme, nvme.Kind);
        Assert.AreEqual(4, nvme.RecommendedQueueDepth);
        Assert.IsTrue(nvme.AllowDirectIo);
        Assert.IsTrue(nvme.AllowPreallocation);
    }

    [TestMethod]
    public void RotationalAndUnknownDevicesStayAtQueueDepthOne()
    {
        var hdd = StorageIoProfile.For(Device("SATA", StorageMediaKind.Rotational, false, true));
        var unknown = StorageIoProfile.For(Device("Unknown", StorageMediaKind.Unknown, false, false));

        Assert.AreEqual(1, hdd.RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Rotational, hdd.Kind);
        Assert.AreEqual(1, unknown.RecommendedQueueDepth);
        Assert.AreEqual(StorageProfileKind.Conservative, unknown.Kind);
    }

    private static StorageDeviceInfo Device(
        string bus,
        StorageMediaKind media,
        bool isNetwork,
        bool preallocation) =>
        new(
            @"C:\\dest",
            @"C:\\",
            isNetwork ? null : 1,
            isNetwork ? null : 1,
            bus,
            media,
            false,
            512,
            4096,
            true,
            null,
            false,
            preallocation ? "NTFS" : "Unknown",
            isNetwork ? DriveType.Network : DriveType.Fixed,
            isNetwork,
            preallocation);
}
