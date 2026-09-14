using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageWritePolicyTests
{
    [TestMethod]
    public void SataSsdLargeExclusiveWriteCanUseQueueDepthEight()
    {
        var device = Device("SATA", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.LargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 8,
            fileSize: 64L * 1024 * 1024,
            dataLength: 32 * 1024 * 1024);

        Assert.AreEqual(8, qd);
    }

    [TestMethod]
    public void NvmeLargeExclusiveWriteCanUseQueueDepthSixteen()
    {
        var device = Device("NVMe", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.LargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 16,
            fileSize: 512L * 1024 * 1024,
            dataLength: 32 * 1024 * 1024);

        Assert.AreEqual(16, qd);
    }

    [TestMethod]
    public void ExactUsbSsdLargeExclusiveWriteCanUseQueueDepthFour()
    {
        var device = Device("USB", StorageMediaKind.SolidState, trim: true);

        var qd = StorageWritePolicy.LargeWriteQueueDepth(
            device,
            schedulerMaxOutstandingIo: 4,
            fileSize: 64L * 1024 * 1024,
            dataLength: 32 * 1024 * 1024);

        Assert.AreEqual(4, qd);
    }

    [TestMethod]
    public void PayloadSizeBoundsUsefulDepthWithoutGlobalQd2Cap()
    {
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);

        Assert.AreEqual(
            8,
            StorageWritePolicy.LargeWriteQueueDepth(
                nvme,
                schedulerMaxOutstandingIo: 16,
                fileSize: 64L * 1024 * 1024,
                dataLength: 8 * 1024 * 1024));

        Assert.AreEqual(
            1,
            StorageWritePolicy.LargeWriteQueueDepth(
                nvme,
                schedulerMaxOutstandingIo: 16,
                fileSize: 8L * 1024 * 1024 - 1,
                dataLength: 8 * 1024 * 1024));
    }

    [TestMethod]
    public void UncertainUsbSsdAndSharedPhysicalDiskStayQueueDepthOne()
    {
        var uncertain = Device("USB", StorageMediaKind.SolidState, trim: true) with
        {
            PhysicalDeviceNumber = null,
        };
        var shared = Device("SATA", StorageMediaKind.SolidState, trim: true) with
        {
            SharesPhysicalDevice = true,
        };

        Assert.AreEqual(
            1,
            StorageWritePolicy.LargeWriteQueueDepth(
                uncertain,
                16,
                64L * 1024 * 1024,
                32 * 1024 * 1024));
        Assert.AreEqual(
            1,
            StorageWritePolicy.LargeWriteQueueDepth(
                shared,
                16,
                64L * 1024 * 1024,
                32 * 1024 * 1024));
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

        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(hdd, 16, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(flash, 16, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(network, 16, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(sata, 16, 64L * 1024 * 1024, 1024 * 1024));
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
