using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSpillPartitionerTests
{
    private const long Target = 128L * 1024 * 1024;

    [TestMethod]
    public void CurrentPolicySpillsOnlyBackpressuredDestinationsWhenPeersRemainNormal()
    {
        var slow = new FanoutSpillController();
        var fast = new FanoutSpillController();

        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(0, Target, Target, 0, slow),
            new FanoutSpillPartitionCandidate(1, Target / 4, Target, 0, fast),
        ]);

        CollectionAssert.AreEqual(new[] { 1 }, result.NormalSlots.ToArray());
        CollectionAssert.AreEqual(new[] { 0 }, result.SpillingSlots.ToArray());
        Assert.AreEqual(FanoutSpillState.Spill, slow.State);
        Assert.AreEqual(FanoutSpillState.Normal, fast.State);
    }

    [TestMethod]
    public void SingleSlowDestinationStaysNormal()
    {
        var flow = new FanoutSpillController();
        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(7, Target, Target, 0, flow),
        ]);
        CollectionAssert.AreEqual(new[] { 7 }, result.NormalSlots.ToArray());
        Assert.AreEqual(0, result.SpillingSlots.Count);
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

    [TestMethod]
    public void AllSlowDestinationsStayNormal()
    {
        var first = new FanoutSpillController();
        var second = new FanoutSpillController();
        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(3, Target, Target, 0, first),
            new FanoutSpillPartitionCandidate(9, Target * 2, Target, 0, second),
        ]);
        CollectionAssert.AreEqual(new[] { 3, 9 }, result.NormalSlots.ToArray());
        Assert.AreEqual(0, result.SpillingSlots.Count);
        Assert.AreEqual(FanoutSpillState.Normal, first.State);
        Assert.AreEqual(FanoutSpillState.Normal, second.State);
    }

    [TestMethod]
    public void ExistingSpillExitsWhenLastNormalPeerBecomesSlow()
    {
        var slow = new FanoutSpillController();
        var peer = new FanoutSpillController();
        FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(0, Target, Target, 0, slow),
            new FanoutSpillPartitionCandidate(1, 0, Target, 0, peer),
        ]);
        Assert.AreEqual(FanoutSpillState.Spill, slow.State);
        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(0, Target, Target, 1024, slow),
            new FanoutSpillPartitionCandidate(1, Target, Target, 0, peer),
        ]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, result.NormalSlots.ToArray());
        Assert.AreEqual(0, result.SpillingSlots.Count);
        Assert.AreEqual(FanoutSpillState.Normal, slow.State);
        Assert.AreEqual(FanoutSpillState.Normal, peer.State);
    }

    [TestMethod]
    public void ExistingSpillExitsWhenItBecomesTheOnlyDestination()
    {
        var flow = new FanoutSpillController();
        flow.ShouldSpill(Target, Target, 0);
        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(4, Target, Target, 1024, flow),
        ]);
        CollectionAssert.AreEqual(new[] { 4 }, result.NormalSlots.ToArray());
        Assert.AreEqual(0, result.SpillingSlots.Count);
        Assert.AreEqual(FanoutSpillState.Normal, flow.State);
    }

    [TestMethod]
    public void MixedPartitionPreservesHysteresisAndRecoversAfterDrain()
    {
        var slow = new FanoutSpillController();
        var fast = new FanoutSpillController();
        slow.ShouldSpill(Target, Target, 0);
        var result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(0, Target / 4, Target, 1, slow),
            new FanoutSpillPartitionCandidate(1, 0, Target, 0, fast),
        ]);
        CollectionAssert.AreEqual(new[] { 0 }, result.SpillingSlots.ToArray());
        result = FanoutSpillPartitioner.Partition([
            new FanoutSpillPartitionCandidate(0, Target / 2, Target, 0, slow),
            new FanoutSpillPartitionCandidate(1, 0, Target, 0, fast),
        ]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, result.NormalSlots.ToArray());
        Assert.AreEqual(FanoutSpillState.Normal, slow.State);
    }

}
