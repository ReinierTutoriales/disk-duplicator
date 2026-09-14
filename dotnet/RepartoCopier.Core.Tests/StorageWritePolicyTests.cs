using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageWritePolicyTests
{
    [TestMethod]
    public void ExactLocalSsdCanExploreFarBeyondLegacyProfileDepths()
    {
        var sata = Device("SATA", StorageMediaKind.SolidState, trim: true);
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);
        var usb = Device("USB", StorageMediaKind.SolidState, trim: true);

        Assert.AreEqual(64, StorageWritePolicy.LargeWriteQueueDepth(
            sata, schedulerExplorationDepth: 64, 256L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(128, StorageWritePolicy.LargeWriteQueueDepth(
            nvme, schedulerExplorationDepth: 128, 512L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(32, StorageWritePolicy.LargeWriteQueueDepth(
            usb, schedulerExplorationDepth: 32, 128L * 1024 * 1024, 32 * 1024 * 1024));
    }

    [TestMethod]
    public void ExactLocalHddAndSharedPhysicalDiskAreNotPermanentlyCappedAtOne()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational, trim: false);
        var shared = Device("SATA", StorageMediaKind.SolidState, trim: true) with
        {
            SharesPhysicalDevice = true,
        };

        Assert.AreEqual(8, StorageWritePolicy.LargeWriteQueueDepth(
            hdd, 8, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(16, StorageWritePolicy.LargeWriteQueueDepth(
            shared, 16, 64L * 1024 * 1024, 32 * 1024 * 1024));
    }

    [TestMethod]
    public void PayloadSizeIsThePracticalExplorationBound()
    {
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);
        var qd = StorageWritePolicy.LargeWriteQueueDepth(
            nvme,
            schedulerExplorationDepth: 16_384,
            fileSize: 64L * 1024 * 1024,
            dataLength: 32 * 1024 * 1024);

        Assert.AreEqual((32 * 1024 * 1024) / StorageWritePolicy.MinimumParallelSliceBytes, qd);
    }

    [TestMethod]
    public void UnknownIdentityNetworkAndSmallFileRemainNonSpeculative()
    {
        var uncertain = Device("USB", StorageMediaKind.SolidState, trim: true) with
        {
            PhysicalDeviceNumber = null,
        };
        var network = Device("Network", StorageMediaKind.Unknown, trim: null) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);

        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(
            uncertain, 128, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(
            network, 128, 64L * 1024 * 1024, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(
            nvme, 128, StorageWritePolicy.ParallelFileThresholdBytes - 1L, 32 * 1024 * 1024));
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
