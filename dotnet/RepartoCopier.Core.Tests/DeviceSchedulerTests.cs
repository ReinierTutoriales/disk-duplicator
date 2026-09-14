using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DeviceSchedulerTests
{
    [TestMethod]
    public void DestinationsOnSamePhysicalDiskShareOneAdaptiveScheduler()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, trim: true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);
        var second = Device(@"F:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [first, second]);
        var scheduler = map.For(first);

        Assert.HasCount(1, map.Schedulers);
        Assert.AreSame(scheduler, map.For(second));
        Assert.AreEqual(8, scheduler.InitialQueueDepth);
        Assert.AreEqual(8, scheduler.CurrentQueueDepth);
        Assert.AreEqual(16, scheduler.ExplorationQueueDepth);
    }

    [TestMethod]
    public void PhysicalCollisionSharesOnlyTheCollidingDiskAndLeavesOtherHardwareIndependent()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, trim: true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);
        var second = Device(@"F:\copy", 4, "USB", StorageMediaKind.SolidState, trim: true);
        var independent = Device(@"G:\copy", 9, "NVMe", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [first, second, independent]);

        Assert.HasCount(2, map.Schedulers);
        Assert.AreSame(map.For(first), map.For(second));
        Assert.AreNotSame(map.For(first), map.For(independent));
        Assert.AreEqual(16, map.For(independent).InitialQueueDepth);
    }

    [TestMethod]
    public void SourceAndDestinationOnSameDiskStartAtOneButAreNotCappedThere()
    {
        var source = Device(@"C:\source", 4, "NVMe", StorageMediaKind.SolidState, trim: true);
        var destination = Device(@"D:\copy", 4, "NVMe", StorageMediaKind.SolidState, trim: true);
        using var map = DeviceSchedulerMap.Create(source, [destination]);
        var scheduler = map.For(destination);

        Assert.AreEqual(1, scheduler.InitialQueueDepth);
        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
        Assert.AreEqual(2, scheduler.ExplorationQueueDepth);
    }

    [TestMethod]
    public async Task SustainedDemandCanGrowBeyondLegacyNvmeQd16()
    {
        using var scheduler = new DeviceScheduler("PhysicalDiskNVMe", 16, 512L * 1024 * 1024);
        var active = new List<DeviceScheduler.IoLease>();
        var waiting = new List<Task<DeviceScheduler.IoLease>>();

        for (var index = 0; index < 16; index++)
            active.Add(await scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None));
        for (var index = 0; index < 16; index++)
            waiting.Add(scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None).AsTask());

        foreach (var lease in active)
            lease.Dispose();
        active.Clear();

        foreach (var task in waiting)
            active.Add(await task.WaitAsync(TimeSpan.FromSeconds(2)));
        foreach (var lease in active)
            lease.Dispose();

        Assert.AreEqual(32, scheduler.CurrentQueueDepth);
        Assert.AreEqual(64, scheduler.ExplorationQueueDepth);
        var snapshot = scheduler.Snapshot();
        Assert.IsGreaterThanOrEqualTo(1, snapshot.QueueDepthUpshifts);
        Assert.IsGreaterThanOrEqualTo(32, snapshot.MaximumObservedQueueDepth);
    }

    [TestMethod]
    public async Task CancelledWaiterDoesNotLeakAdaptiveCapacity()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk11", 1, 32L * 1024 * 1024);
        using var first = await scheduler.AcquireIoAsync(4096, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.AcquireIoAsync(4096, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting);
        Assert.AreEqual(1, scheduler.OutstandingIo);
    }

    [TestMethod]
    public void SoftBacklogTargetIsAccountingOnlyAndAllowsImmediateOverflow()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, block);

        scheduler.ReserveBacklog(block);
        scheduler.ReserveBacklog(block);

        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(2L * block, snapshot.QueuedBytes);
        Assert.AreEqual(2.0, snapshot.BacklogPressure, 0.000001);
        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
    }

    [TestMethod]
    public void SnapshotExposesAdaptiveWindowRatherThanFixedMaximum()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk12", 8, 64L * 1024 * 1024);
        var snapshot = scheduler.Snapshot();

        Assert.AreEqual(8, snapshot.InitialQueueDepth);
        Assert.AreEqual(8, snapshot.CurrentQueueDepth);
        Assert.AreEqual(16, snapshot.ExplorationQueueDepth);
        Assert.AreEqual(8, snapshot.BestObservedQueueDepth);
    }

    private static StorageDeviceInfo Device(string root, uint physicalDisk, string bus, StorageMediaKind media, bool? trim) =>
        new(root, Path.GetPathRoot(root)!, physicalDisk, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
