using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SourceReadTuningTests
{
    [TestMethod]
    public void ExactLocalNvmeStartsAtThirtyTwoMiBAndFourOfEightPrefetch()
    {
        var tuning = SourceReadTuning.For(Device("NVMe", StorageMediaKind.SolidState, trim: true));

        Assert.AreEqual(32 * 1024 * 1024, tuning.BlockSizeBytes);
        Assert.AreEqual(8, tuning.PrefetchPhysicalCapacity);
        Assert.AreEqual(4, tuning.HashPipelineCapacity);
        Assert.AreEqual(4, tuning.InitialPrefetch);
        Assert.AreEqual(8, tuning.MaximumPrefetch);
    }

    [TestMethod]
    public void ExactLocalSataAndUsbSsdUseAggressiveProfile()
    {
        var sata = SourceReadTuning.For(Device("SATA", StorageMediaKind.SolidState, trim: true));
        var usb = SourceReadTuning.For(Device("USB", StorageMediaKind.SolidState, trim: true));

        Assert.AreEqual(32 * 1024 * 1024, sata.BlockSizeBytes);
        Assert.AreEqual(8, sata.MaximumPrefetch);
        Assert.AreEqual(32 * 1024 * 1024, usb.BlockSizeBytes);
        Assert.AreEqual(8, usb.MaximumPrefetch);
    }

    [TestMethod]
    public void UncertainUsbSsdHddAndNetworkRemainConservative()
    {
        var uncertainUsb = Device("USB", StorageMediaKind.SolidState, trim: true) with
        {
            PhysicalDeviceNumber = null,
        };
        var hdd = Device("SATA", StorageMediaKind.Rotational, trim: false);
        var network = Device("Network", StorageMediaKind.Unknown, trim: null) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };

        foreach (var tuning in new[]
                 {
                     SourceReadTuning.For(uncertainUsb),
                     SourceReadTuning.For(hdd),
                     SourceReadTuning.For(network),
                 })
        {
            Assert.AreEqual(16 * 1024 * 1024, tuning.BlockSizeBytes);
            Assert.AreEqual(4, tuning.PrefetchPhysicalCapacity);
            Assert.AreEqual(2, tuning.HashPipelineCapacity);
            Assert.AreEqual(2, tuning.InitialPrefetch);
            Assert.AreEqual(4, tuning.MaximumPrefetch);
        }
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(
            @"C:\source",
            @"C:\",
            1,
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
