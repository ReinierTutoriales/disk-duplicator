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
}
