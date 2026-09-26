using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSizingTests
{
    [TestMethod]
    public void SharedPoolHoldsThirtyTwoFullStreamingBlocks()
    {
        var pool = new FanoutSpillBudget(256L * 1024 * 1024);
        var block = 8L * 1024 * 1024;
        Assert.AreEqual(32L, pool.CapacityBytes / block);
    }

    [TestMethod]
    public void EightMiBBlockProvidesUsefulConcurrencyAtFixedQueueDepths()
    {
        var profiles = new[]
        {
            StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.Rotational, false)),
        };
        const long block = 8L * 1024 * 1024;
        CollectionAssert.AreEqual(
            new[] { 64L, 32L, 16L },
            profiles.Select(profile => block * profile.InitialQueueDepth / (1024 * 1024)).ToArray());
    }

    [TestMethod]
    public void FiveWaySpillFairShareStillBuffersMoreThanTwelveFullBlocks()
    {
        var budget = new FanoutSpillBudget();
        var ceiling = budget.DestinationCeiling(5, 512L * 1024 * 1024);
        const long block = 8L * 1024 * 1024;
        Assert.IsTrue(ceiling / block >= 12);
        Assert.IsTrue(ceiling <= 512L * 1024 * 1024 / 5);
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(@"C:\\dest", @"C:\\", 1, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
