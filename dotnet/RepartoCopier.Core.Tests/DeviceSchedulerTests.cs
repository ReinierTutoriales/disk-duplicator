using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DeviceSchedulerTests
{
    [TestMethod]
    public void DestinationsOnSamePhysicalDiskShareOneScheduler()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, trim: true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);
        var second = Device(@"F:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [first, second]);

        Assert.HasCount(1, map.Schedulers);
        Assert.AreSame(map.For(first), map.For(second));
        Assert.AreEqual(2, map.For(first).MaxOutstandingIo);
        Assert.AreEqual(DeviceIdentityConfidence.Exact, map.For(first).IdentityConfidence);
    }

    [TestMethod]
    public void DifferentPhysicalDisksRemainIndependent()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, trim: true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);
        var second = Device(@"F:\copy", 7, "SATA", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [first, second]);

        Assert.HasCount(2, map.Schedulers);
        Assert.AreNotSame(map.For(first), map.For(second));
    }

    [TestMethod]
    public void UnknownPhysicalDiskPropagatesPartialIdentityConfidence()
    {
        var destination = new StorageDeviceInfo(
            @"E:\copy",
            @"E:\",
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            "probe failed");

        using var map = DeviceSchedulerMap.Create(null, [destination]);

        var scheduler = map.For(destination);
        Assert.AreEqual(DeviceIdentityConfidence.Partial, scheduler.IdentityConfidence);
        Assert.AreEqual(DeviceIdentityConfidence.Partial, scheduler.Snapshot().IdentityConfidence);
        Assert.AreEqual(1, scheduler.MaxOutstandingIo);
    }

    [TestMethod]
    public void SourceAndDestinationOnSameDiskForceQueueDepthOne()
    {
        var source = Device(@"C:\source", 4, "NVMe", StorageMediaKind.SolidState, trim: true);
        var destination = Device(@"D:\copy", 4, "NVMe", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [destination]);

        Assert.AreEqual(1, map.For(destination).MaxOutstandingIo);
    }

    [TestMethod]
    public async Task IoLeaseEnforcesAggregatePhysicalQueueDepth()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk9", 1, 32L * 1024 * 1024);
        using var first = await scheduler.AcquireIoAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            using var _ = await scheduler.AcquireIoAsync(cancellation.Token);
        });

        Assert.AreEqual(1, scheduler.OutstandingIo);
        Assert.AreEqual(1, scheduler.PeakOutstandingIo);
    }

    [TestMethod]
    public async Task BacklogReservationWaitsUntilPhysicalQueueDrains()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, block);

        Assert.IsTrue(scheduler.TryReserveBacklog(block));
        Assert.IsFalse(scheduler.TryReserveBacklog(block));

        var waiting = scheduler.ReserveBacklogAsync(block, CancellationToken.None).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(block, scheduler.QueuedBytes);

        scheduler.ReleaseBacklog(block);
        await waiting;

        Assert.AreEqual(block, scheduler.QueuedBytes);
        Assert.AreEqual(block, scheduler.PeakQueuedBytes);
        scheduler.ReleaseBacklog(block);
        Assert.AreEqual(0, scheduler.QueuedBytes);
    }

    [TestMethod]
    public void EmptyQueueAllowsOneBlockLargerThanConservativeTarget()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk8", 1, 4L * 1024 * 1024);

        Assert.IsTrue(scheduler.TryReserveBacklog(16 * 1024 * 1024));
        Assert.IsFalse(scheduler.TryReserveBacklog(1));
        scheduler.ReleaseBacklog(16 * 1024 * 1024);
        Assert.AreEqual(0, scheduler.QueuedBytes);
    }

    [TestMethod]
    public void QueueTelemetryTracksPeakWithoutEnforcingYet()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, 16L * 1024 * 1024);

        scheduler.NoteQueuedBytes(8 * 1024 * 1024);
        scheduler.NoteQueuedBytes(4 * 1024 * 1024);
        scheduler.NoteDequeuedBytes(8 * 1024 * 1024);

        Assert.AreEqual(4L * 1024 * 1024, scheduler.QueuedBytes);
        Assert.AreEqual(12L * 1024 * 1024, scheduler.PeakQueuedBytes);
    }

    private static StorageDeviceInfo Device(
        string root,
        uint physicalDisk,
        string bus,
        StorageMediaKind media,
        bool? trim) =>
        new(
            root,
            Path.GetPathRoot(root)!,
            physicalDisk,
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
