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
