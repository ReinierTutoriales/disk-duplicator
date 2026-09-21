using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSizingTests
{
    [TestMethod]
    public void SharedPoolHoldsThirtyTwoFullStreamingBlocks()
    {
        const long pool = 256L * 1024 * 1024;
        const int block = 8 * 1024 * 1024;
        Assert.AreEqual(32L, pool / block);
    }

    [TestMethod]
    public void EightMiBBlockProvidesUsefulConcurrencyAtFixedQueueDepths()
    {
        const long block = 8L * 1024 * 1024;
        Assert.AreEqual(64L * 1024 * 1024, block * 8); // NVMe QD8
        Assert.AreEqual(32L * 1024 * 1024, block * 4); // SSD QD4
        Assert.AreEqual(16L * 1024 * 1024, block * 2); // HDD/network QD2
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
}
