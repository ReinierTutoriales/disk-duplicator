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
}
