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
        Assert.AreEqual(8, map.For(first).MaxOutstandingIo);
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
            @"E:\copy", @"E:\", null, null, "Unknown", StorageMediaKind.Unknown,
            null, null, null, false, "probe failed");

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
    public async Task IndependentIoLeasesScaleToConfiguredQueueDepth()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk10", 4, 32L * 1024 * 1024);
        var leases = new List<DeviceScheduler.IoLease>();
        try
        {
            for (var index = 0; index < 4; index++)
                leases.Add(await scheduler.AcquireIoAsync(CancellationToken.None));

            Assert.AreEqual(4, scheduler.OutstandingIo);
            Assert.AreEqual(4, scheduler.PeakOutstandingIo);

            var fifthTask = scheduler.AcquireIoAsync(CancellationToken.None).AsTask();
            await Task.Delay(50);
            Assert.IsFalse(fifthTask.IsCompleted);

            leases[0].Dispose();
            leases.RemoveAt(0);
            using var fifth = await fifthTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(4, scheduler.OutstandingIo);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }

    [TestMethod]
    public async Task CancelledIoWaitNeverLeaksPhysicalCapacity()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk11", 2, 32L * 1024 * 1024);
        using var first = await scheduler.AcquireIoAsync(CancellationToken.None);
        using var second = await scheduler.AcquireIoAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = scheduler.AcquireIoAsync(cancellation.Token).AsTask();
        await Task.Delay(50);
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
        Assert.AreEqual(2, scheduler.OutstandingIo);
        Assert.AreEqual(2, scheduler.PeakOutstandingIo);
    }

    [TestMethod]
    public async Task SoftBacklogTargetDoesNotBlockOverflowAdmission()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, block);

        Assert.IsTrue(scheduler.TryReserveBacklog(block));
        Assert.IsFalse(scheduler.TryReserveBacklog(block));
        var overflow = scheduler.ReserveBacklogAsync(block, CancellationToken.None);
        Assert.IsTrue(overflow.IsCompletedSuccessfully, "Una rama sobre el soft watermark no puede bloquear el productor FAN-OUT.");
        await overflow;

        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(2L * block, snapshot.QueuedBytes);
        Assert.AreEqual(2L * block, snapshot.PeakQueuedBytes);
        Assert.AreEqual(2.0, snapshot.BacklogPressure, 0.000001);

        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
        Assert.AreEqual(0, scheduler.QueuedBytes);
    }

    [TestMethod]
    public void EmptyQueueStillAdmitsOneBlockLargerThanSoftTarget()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk8", 1, 4L * 1024 * 1024);

        Assert.IsTrue(scheduler.TryReserveBacklog(16 * 1024 * 1024));
        Assert.IsFalse(scheduler.TryReserveBacklog(1));
        scheduler.ReleaseBacklog(16 * 1024 * 1024);
        Assert.AreEqual(0, scheduler.QueuedBytes);
    }

    [TestMethod]
    public async Task OverflowAdmissionHonorsCancellationBeforeReservation()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk12", 1, 8L * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await scheduler.ReserveBacklogAsync(8 * 1024 * 1024, cancellation.Token));

        Assert.AreEqual(0, scheduler.QueuedBytes);
        Assert.AreEqual(0, scheduler.PeakQueuedBytes);
    }

    private static StorageDeviceInfo Device(string root, uint physicalDisk, string bus, StorageMediaKind media, bool? trim) =>
        new(root, Path.GetPathRoot(root)!, physicalDisk, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
