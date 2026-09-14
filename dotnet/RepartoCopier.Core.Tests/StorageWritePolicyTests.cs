using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageWritePolicyTests
{
    [TestMethod]
    public void SataSsdLargeExclusiveWriteUsesQueueDepthTwo()
    {
        var device = Device("SATA", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.BufferedLargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 2,
            fileSize: 64L * 1024 * 1024,
            dataLength: 16 * 1024 * 1024);

        Assert.AreEqual(2, qd);
    }

    [TestMethod]
    public void NvmeLargeExclusiveWriteUsesQueueDepthTwoNotFour()
    {
        var device = Device("NVMe", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.BufferedLargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 4,
            fileSize: 512L * 1024 * 1024,
            dataLength: 16 * 1024 * 1024);

        Assert.AreEqual(2, qd);
    }

    [TestMethod]
    public void ExactFixedUsbSsdLargeExclusiveWriteUsesQueueDepthTwo()
    {
        var device = Device("USB", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.BufferedLargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 2,
            fileSize: 64L * 1024 * 1024,
            dataLength: 16 * 1024 * 1024);

        Assert.AreEqual(2, qd);
    }

    [TestMethod]
    public void RemovableOrUncertainUsbSsdStaysQueueDepthOne()
    {
        var removable = Device("USB", StorageMediaKind.SolidState, trim: true) with
        {
            Removable = true,
        };
        var uncertain = Device("USB", StorageMediaKind.SolidState, trim: true) with
        {
            PhysicalDeviceNumber = null,
        };

        Assert.AreEqual(
            1,
            StorageWritePolicy.BufferedLargeWriteQueueDepth(
                removable,
                2,
                64L * 1024 * 1024,
                16 * 1024 * 1024));
        Assert.AreEqual(
            1,
            StorageWritePolicy.BufferedLargeWriteQueueDepth(
                uncertain,
                2,
                64L * 1024 * 1024,
                16 * 1024 * 1024));
    }

    [TestMethod]
    public void SharedPhysicalDiskStaysQueueDepthOne()
    {
        var device = Device("SATA", StorageMediaKind.SolidState, trim: true) with
        {
            SharesPhysicalDevice = true,
        };

        Assert.AreEqual(
            1,
            StorageWritePolicy.BufferedLargeWriteQueueDepth(
                device,
                2,
                64L * 1024 * 1024,
                16 * 1024 * 1024));
    }

    [TestMethod]
    public void HddFlashNetworkAndSmallTailStayQueueDepthOne()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational, trim: false);
        var flash = Device("USB", StorageMediaKind.SolidState, trim: false) with
        {
            Removable = true,
        };
        var network = Device("Network", StorageMediaKind.Unknown, trim: null) with
        {
            IsNetwork = true,
        };
        var sata = Device("SATA", StorageMediaKind.SolidState, trim: true);

        Assert.AreEqual(1, StorageWritePolicy.BufferedLargeWriteQueueDepth(hdd, 2, 64L * 1024 * 1024, 16 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.BufferedLargeWriteQueueDepth(flash, 2, 64L * 1024 * 1024, 16 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.BufferedLargeWriteQueueDepth(network, 2, 64L * 1024 * 1024, 16 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.BufferedLargeWriteQueueDepth(sata, 2, 64L * 1024 * 1024, 1024 * 1024));
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(
            @"E:\copy",
            @"E:\",
            4,
            1,
            bus,
            media,
            false,
            512,
            4096,
            true,
            null,
            false,
            "NTFS",
            DriveType.Fixed,
            false,
            true,
            trim,
            0);
}
