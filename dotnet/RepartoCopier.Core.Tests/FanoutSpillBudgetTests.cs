using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSpillBudgetTests
{
    [TestMethod]
    public void DestinationCeilingUsesFairShareFloorAndBacklogCeiling()
    {
        var budget = new FanoutSpillBudget(512L * 1024 * 1024);
        Assert.AreEqual(256L * 1024 * 1024, budget.DestinationCeiling(2, 512L * 1024 * 1024));
        Assert.AreEqual(32L * 1024 * 1024, budget.DestinationCeiling(20, 256L * 1024 * 1024));
        Assert.AreEqual(16L * 1024 * 1024, budget.DestinationCeiling(64, 256L * 1024 * 1024));
        Assert.AreEqual(32L * 1024 * 1024, budget.DestinationCeiling(1, 32L * 1024 * 1024));
    }

    [TestMethod]
    public void ReservationsNeverExceedGlobalCapacityAndFullyRelease()
    {
        var budget = new FanoutSpillBudget(24L * 1024 * 1024);
        Assert.IsTrue(budget.TryReserve(8 * 1024 * 1024));
        Assert.IsTrue(budget.TryReserve(8 * 1024 * 1024));
        Assert.IsTrue(budget.TryReserve(8 * 1024 * 1024));
        Assert.IsFalse(budget.TryReserve(1));
        Assert.AreEqual(24L * 1024 * 1024, budget.UsedBytes);
        Assert.AreEqual(budget.UsedBytes, budget.PeakUsedBytes);
        budget.Release(8 * 1024 * 1024);
        budget.Release(16 * 1024 * 1024);
        Assert.AreEqual(0L, budget.UsedBytes);
    }

    [TestMethod]
    public void MixedFiveDestinationBudgetCannotExceedGlobalCap()
    {
        var budget = new FanoutSpillBudget();
        var ceiling = budget.DestinationCeiling(5, 512L * 1024 * 1024);
        Assert.AreEqual(102L * 1024 * 1024 + 2L * 1024 * 1024 / 5, ceiling);

        var reserved = new List<int>();
        const int block = 8 * 1024 * 1024;
        while (budget.TryReserve(block))
            reserved.Add(block);

        Assert.IsTrue(budget.UsedBytes <= FanoutSpillBudget.DefaultCapacityBytes);
        Assert.IsTrue(budget.PeakUsedBytes <= FanoutSpillBudget.DefaultCapacityBytes);
        Assert.IsFalse(budget.TryReserve(block));

        foreach (var bytes in reserved)
            budget.Release(bytes);
        Assert.AreEqual(0, budget.UsedBytes);
    }

    [TestMethod]
    public void FairShareGrowsWhenDestinationsBecomeInactive()
    {
        var budget = new FanoutSpillBudget();
        var active = 8;
        long Ceiling() => budget.DestinationCeilingForCurrentActiveCount(() => active, 512L * 1024 * 1024);

        Assert.AreEqual(64L * 1024 * 1024, Ceiling());
        active = 4;
        Assert.AreEqual(128L * 1024 * 1024, Ceiling());
        active = 2;
        Assert.AreEqual(256L * 1024 * 1024, Ceiling());
        active = 1;
        Assert.AreEqual(512L * 1024 * 1024, Ceiling());
    }

}
