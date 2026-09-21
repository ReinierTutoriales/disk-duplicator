using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageIoProfileTests
{
    [TestMethod]
    public void StorageProfilesUseFixedQueueDepthByHardwareClass()
    {
        var profiles = new[]
        {
            StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, false)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.Rotational, false)),
            StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("Network", StorageMediaKind.Unknown, null, isNetwork: true)),
            StorageIoProfile.For(Device("Unknown", StorageMediaKind.Unknown, null)),
        };
        CollectionAssert.AreEqual(new[] { 4, 1, 4, 2, 8, 2, 1 }, profiles.Select(profile => profile.InitialQueueDepth).ToArray());
    }

    [TestMethod]
    public void UsbSsdUsesQueueDepthFourAndKeepsItsBacklogBudget()
    {
        var usb = StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, true));
        Assert.AreEqual(StorageProfileKind.UsbSsd, usb.Kind);
        Assert.AreEqual(4, usb.InitialQueueDepth);
        Assert.AreEqual(256L * 1024 * 1024, usb.DeviceBacklogTargetBytes);
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(@"C:\dest", @"C:\", 1, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
