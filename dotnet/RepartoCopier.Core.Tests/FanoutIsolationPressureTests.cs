using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutIsolationPressureTests
{
    private const int MiB = 1024 * 1024;
    private const int Block = 8 * MiB;

    [TestMethod]
    public void OneLaggingDestinationCannotRetainSharedPagesOwnedByFourFastDestinations()
    {
        using var pool = new SharedFanoutBufferPool(32 * MiB);
        var slow = new FanoutSpillController();

        Assert.IsTrue(slow.ShouldSpill(128L * MiB, 128L * MiB, 0));

        var fastLeases = new List<SharedFanoutBufferPool.Lease>();
        for (var i = 0; i < 4; i++)
        {
            var lease = pool.RentAsync(Block, 4096, references: 4, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            fastLeases.Add(lease);
        }
        Assert.AreEqual(32 * MiB, pool.UsedBytes);

        // The slow branch is deliberately absent from the shared reference count.
        // Once the four healthy branches release, every page is reusable immediately.
        foreach (var lease in fastLeases)
        {
            for (var fastDestination = 0; fastDestination < 4; fastDestination++)
                lease.ReleaseReference();
        }
        Assert.AreEqual(0, pool.UsedBytes);

        using var next = pool.RentAsync(Block, 4096, references: 4, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.AreEqual(Block, pool.UsedBytes);
        for (var fastDestination = 0; fastDestination < 4; fastDestination++)
            next.ReleaseReference();
        Assert.AreEqual(0, pool.UsedBytes);
    }

    [TestMethod]
    public void SpillPressureNeverChangesFixedHardwareQueueDepth()
    {
        using var scheduler = new DeviceScheduler("nvme", 8, 512L * MiB);
        var spill = new FanoutSpillController();

        scheduler.ReserveBacklog(512 * MiB);
        Assert.IsTrue(spill.ShouldSpill(scheduler.QueuedBytes, scheduler.BacklogTargetBytes, 8L * MiB));

        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(8, snapshot.CurrentQueueDepth);
        Assert.AreEqual(8, snapshot.ExplorationQueueDepth);
        Assert.AreEqual(0, snapshot.QueueDepthDownshifts);
        Assert.AreEqual("fixed:storage-profile", snapshot.LastQueueDepthDecision);

        scheduler.ReleaseBacklog(512 * MiB);
    }

    [TestMethod]
    public void SlowBranchCanRecoverOnlyAfterPrivatePayloadFullyDrains()
    {
        var spill = new FanoutSpillController();
        Assert.IsTrue(spill.ShouldSpill(128L * MiB, 128L * MiB, 0));

        Assert.IsTrue(spill.ShouldSpill(32L * MiB, 128L * MiB, Block));
        Assert.AreEqual(FanoutSpillState.Spill, spill.State);

        Assert.IsFalse(spill.ShouldSpill(32L * MiB, 128L * MiB, 0));
        Assert.AreEqual(FanoutSpillState.Normal, spill.State);
    }
}
