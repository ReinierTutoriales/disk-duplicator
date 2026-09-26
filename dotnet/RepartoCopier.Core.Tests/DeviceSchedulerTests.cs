using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DeviceSchedulerTests
{
    [TestMethod]
    public void DestinationsOnSamePhysicalDiskShareOneFixedScheduler()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, true);
        var second = Device(@"F:\copy", 4, "USB", StorageMediaKind.SolidState, true);
        using var map = DeviceSchedulerMap.Create(source, [first, second]);
        Assert.HasCount(1, map.Schedulers);
        Assert.AreSame(map.For(first), map.For(second));
        Assert.AreEqual(4, map.For(first).CurrentQueueDepth);
        Assert.AreEqual(4, map.For(first).ExplorationQueueDepth);
    }

    [TestMethod]
    public async Task QueueDepthTwoAllowsTwoIosAndBlocksThird()
    {
        using var scheduler = new DeviceScheduler("PhysicalDiskHdd", 2, 128L * 1024 * 1024);
        using var first = await scheduler.AcquireIoAsync(8 * 1024 * 1024, CancellationToken.None);
        using var second = await scheduler.AcquireIoAsync(8 * 1024 * 1024, CancellationToken.None);
        var thirdTask = scheduler.AcquireIoAsync(8 * 1024 * 1024, CancellationToken.None).AsTask();
        Assert.IsFalse(thirdTask.IsCompleted);
        first.Dispose();
        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, scheduler.OutstandingIo);
        Assert.AreEqual(2, scheduler.PeakOutstandingIo);
    }

    [TestMethod]
    public void SnapshotStaysFixedInsteadOfExploring()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk", 8, 512L * 1024 * 1024);
        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(8, snapshot.InitialQueueDepth);
        Assert.AreEqual(8, snapshot.CurrentQueueDepth);
        Assert.AreEqual(8, snapshot.ExplorationQueueDepth);
        Assert.AreEqual(0, snapshot.QueueDepthUpshifts);
        Assert.AreEqual("fixed:storage-profile", snapshot.LastQueueDepthDecision);
    }

    [TestMethod]
    public void SoftBacklogTargetRemainsAccountingOnly()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk", 1, block);
        scheduler.ReserveBacklog(block);
        scheduler.ReserveBacklog(block);
        Assert.AreEqual(2L * block, scheduler.Snapshot().QueuedBytes);
        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
    }

    private static StorageDeviceInfo Device(string root, uint physicalDisk, string bus, StorageMediaKind media, bool? trim) =>
        new(root, Path.GetPathRoot(root)!, physicalDisk, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
