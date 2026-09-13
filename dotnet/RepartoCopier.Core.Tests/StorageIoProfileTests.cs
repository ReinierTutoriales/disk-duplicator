using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageIoProfileTests
{
    [TestMethod]
    public void NetworkAndUsbFlashRemainConservative()
    {
        var network = Device("Network", StorageMediaKind.Unknown, isNetwork: true, preallocation: false);
        var usbFlash = Device("USB", StorageMediaKind.SolidState, isNetwork: false, preallocation: false, trim: false, removable: true);

        var networkProfile = StorageIoProfile.For(network);
        Assert.AreEqual(1, networkProfile.RecommendedQueueDepth);
        Assert.AreEqual(1, networkProfile.MaximumQueueDepth);
        Assert.AreEqual(StorageProfileKind.Network, networkProfile.Kind);
        Assert.IsFalse(networkProfile.AllowDirectIo);
        Assert.IsFalse(networkProfile.AllowPreallocation);

        var flashProfile = StorageIoProfile.For(usbFlash);
        Assert.AreEqual(1, flashProfile.RecommendedQueueDepth);
        Assert.AreEqual(1, flashProfile.MaximumQueueDepth);
        Assert.AreEqual(StorageProfileKind.UsbFlash, flashProfile.Kind);
        Assert.IsFalse(flashProfile.AllowDirectIo);
        Assert.IsFalse(flashProfile.AllowPreallocation);
    }

    [TestMethod]
    public void UsbSsdCanBeBenchmarkedButStartsAtQueueDepthOne()
    {
        var device = Device("USB", StorageMediaKind.SolidState, false, true, trim: true, removable: false);

        var profile = StorageIoProfile.For(device);

        Assert.AreEqual(StorageProfileKind.UsbSsd, profile.Kind);
        Assert.AreEqual(1, profile.RecommendedQueueDepth);
        Assert.AreEqual(2, profile.MaximumQueueDepth);
        Assert.IsTrue(profile.BenchmarkCanRaiseQueueDepth);
        Assert.IsTrue(profile.AllowPreallocation);
        Assert.IsFalse(profile.AllowDirectIo);
    }

    [TestMethod]
    public void SataAndNvmeUseConservativeStaticDefaults()
    {
        var sata = StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, false, true, trim: true));
        var nvme = StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, false, true, trim: true, alignmentOffset: 0));

        Assert.AreEqual(StorageProfileKind.SataSsd, sata.Kind);
        Assert.AreEqual(2, sata.RecommendedQueueDepth);
        Assert.AreEqual(2, sata.MaximumQueueDepth);
        Assert.IsFalse(sata.AllowDirectIo);
        Assert.IsTrue(sata.AllowPreallocation);

        Assert.AreEqual(StorageProfileKind.Nvme, nvme.Kind);
        Assert.AreEqual(2, nvme.RecommendedQueueDepth);
        Assert.AreEqual(4, nvme.MaximumQueueDepth);
        Assert.IsTrue(nvme.BenchmarkCanRaiseQueueDepth);
        Assert.IsTrue(nvme.AllowDirectIo);
        Assert.IsTrue(nvme.AllowPreallocation);
    }

    [TestMethod]
    public void NvmeDirectIoRequiresKnownAlignmentAndSafeFilesystem()
    {
        var missingAlignment = StorageIoProfile.For(
            Device("NVMe", StorageMediaKind.SolidState, false, true, trim: true, alignmentOffset: null));
        var unsafeFilesystem = StorageIoProfile.For(
            Device("NVMe", StorageMediaKind.SolidState, false, false, trim: true, alignmentOffset: 0));

        Assert.IsFalse(missingAlignment.AllowDirectIo);
        Assert.IsFalse(unsafeFilesystem.AllowDirectIo);
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
