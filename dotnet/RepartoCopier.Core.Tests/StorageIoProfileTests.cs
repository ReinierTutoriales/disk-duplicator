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
                nameof(StorageIoProfile.InitialQueueDepth),
                nameof(StorageIoProfile.Kind),
            },
            properties);
    }

    [TestMethod]
    public void ConservativeClassesStartAtQueueDepthOne()
    {
        var network = Device("Network", StorageMediaKind.Unknown, isNetwork: true, preallocation: false);
        var usbFlash = Device("USB", StorageMediaKind.SolidState, isNetwork: false, preallocation: false, trim: false, removable: true);
        var hdd = Device("SATA", StorageMediaKind.Rotational, isNetwork: false, preallocation: true);

        Assert.AreEqual(1, StorageIoProfile.For(network).InitialQueueDepth);
        Assert.AreEqual(1, StorageIoProfile.For(usbFlash).InitialQueueDepth);
        Assert.AreEqual(1, StorageIoProfile.For(hdd).InitialQueueDepth);
    }

    [TestMethod]
    public void ExactUsbSsdStartsAtFourEvenWhenRemovable()
    {
        var fixedDevice = Device("USB", StorageMediaKind.SolidState, false, true, trim: true, removable: false);
        var removableDevice = Device("USB", StorageMediaKind.SolidState, false, true, trim: true, removable: true);

        foreach (var device in new[] { fixedDevice, removableDevice })
        {
            var profile = StorageIoProfile.For(device);
            Assert.AreEqual(StorageProfileKind.UsbSsd, profile.Kind);
            Assert.AreEqual(4, profile.InitialQueueDepth);
            Assert.AreEqual(256L * 1024 * 1024, profile.DeviceBacklogTargetBytes);
        }
    }

    [TestMethod]
    public void UncertainUsbSsdStartsAtOne()
    {
        var uncertain = Device(
            "USB",
            StorageMediaKind.SolidState,
            false,
            true,
            trim: true,
            removable: true,
            physicalDisk: null);

        var profile = StorageIoProfile.For(uncertain);
        Assert.AreEqual(StorageProfileKind.UsbSsd, profile.Kind);
        Assert.AreEqual(1, profile.InitialQueueDepth);
    }

    [TestMethod]
    public void SataAndNvmeExposeAggressiveStartingDepthsNotMaximums()
    {
        var sata = StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, false, true, trim: true));
        var nvme = StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, false, true, trim: true, alignmentOffset: 0));

        Assert.AreEqual(8, sata.InitialQueueDepth);
        Assert.AreEqual(16, nvme.InitialQueueDepth);
    }

    [TestMethod]
    public void UnknownLocalDeviceStartsAtOneAndConservative()
    {
        var unknown = StorageIoProfile.For(Device("Unknown", StorageMediaKind.Unknown, false, false));
        Assert.AreEqual(StorageProfileKind.Conservative, unknown.Kind);
        Assert.AreEqual(1, unknown.InitialQueueDepth);
    }

    private static StorageDeviceInfo Device(
        string bus,
        StorageMediaKind media,
        bool isNetwork,
        bool preallocation,
        bool? trim = null,
        bool? removable = false,
        uint? alignmentOffset = null,
        uint? physicalDisk = 1) =>
        new(
            @"C:\dest",
            @"C:\",
            isNetwork ? null : physicalDisk,
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
