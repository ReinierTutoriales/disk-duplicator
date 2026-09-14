using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageWritePolicyTests
{
    [TestMethod]
    public void LocalDevicesCanExploreFarBeyondLegacyProfileDepths()
    {
        var sata = Device("SATA", StorageMediaKind.SolidState, trim: true);
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);
        var usb = Device("USB", StorageMediaKind.SolidState, trim: true);
        var hdd = Device("SATA", StorageMediaKind.Rotational, trim: false);

        Assert.AreEqual(64, StorageWritePolicy.LargeWriteQueueDepth(sata, 64, 32 * 1024 * 1024));
        Assert.AreEqual(128, StorageWritePolicy.LargeWriteQueueDepth(nvme, 128, 32 * 1024 * 1024));
        Assert.AreEqual(32, StorageWritePolicy.LargeWriteQueueDepth(usb, 32, 32 * 1024 * 1024));
        Assert.AreEqual(8, StorageWritePolicy.LargeWriteQueueDepth(hdd, 8, 32 * 1024 * 1024));
    }

    [TestMethod]
    public void SmallPayloadIsNotForcedToQdOneByFileSizePolicy()
    {
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);
        const int payload = 256 * 1024;

        var qd = StorageWritePolicy.LargeWriteQueueDepth(nvme, schedulerExplorationDepth: 64, dataLength: payload);

        Assert.AreEqual(payload / StorageWritePolicy.MinimumParallelSliceBytes, qd);
        Assert.IsGreaterThan(1, qd);
    }

    [TestMethod]
    public void PayloadSizeIsThePracticalExplorationBound()
    {
        var nvme = Device("NVMe", StorageMediaKind.SolidState, trim: true);
        var qd = StorageWritePolicy.LargeWriteQueueDepth(
            nvme,
            schedulerExplorationDepth: 16_384,
            dataLength: 32 * 1024 * 1024);

        Assert.AreEqual((32 * 1024 * 1024) / StorageWritePolicy.MinimumParallelSliceBytes, qd);
    }

    [TestMethod]
    public void UncertainLocalIdentityDoesNotForceQdOneButNetworkStillUsesBufferedSerialPolicy()
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

        Assert.AreEqual(128, StorageWritePolicy.LargeWriteQueueDepth(uncertain, 128, 32 * 1024 * 1024));
        Assert.AreEqual(1, StorageWritePolicy.LargeWriteQueueDepth(network, 128, 32 * 1024 * 1024));
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
