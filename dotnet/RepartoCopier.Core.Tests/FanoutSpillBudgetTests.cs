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
        Assert.AreEqual(512L * 1024 * 1024 / 20, budget.DestinationCeiling(20, 256L * 1024 * 1024));
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


    [TestMethod]
    public async Task ConcurrentReservationsCannotCrossGlobalCap()
    {
        var budget = new FanoutSpillBudget(64L * 1024 * 1024);
        const int block = 1 * 1024 * 1024;
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            var held = 0;
            while (budget.TryReserve(block))
                held += block;
            return held;
        })).ToArray();

        start.Set();
        var heldByWorkers = await Task.WhenAll(tasks);

        Assert.AreEqual(64L * 1024 * 1024, heldByWorkers.Sum(value => (long)value));
        Assert.AreEqual(64L * 1024 * 1024, budget.UsedBytes);
        Assert.AreEqual(64L * 1024 * 1024, budget.PeakUsedBytes);
        Assert.IsFalse(budget.TryReserve(1));

        foreach (var held in heldByWorkers)
            if (held > 0)
                budget.Release(held);
        Assert.AreEqual(0L, budget.UsedBytes);
    }

    [TestMethod]
    public async Task LiveFairShareChangesCannotBypassAtomicGlobalBudget()
    {
        var budget = new FanoutSpillBudget(64L * 1024 * 1024);
        var active = 8;
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            start.Wait();
            if (index == 0)
                Interlocked.Exchange(ref active, 1);

            var ceiling = budget.DestinationCeilingForCurrentActiveCount(
                () => Volatile.Read(ref active),
                64L * 1024 * 1024);

            long held = 0;
            const int block = 1 * 1024 * 1024;
            while (held + block <= ceiling && budget.TryReserve(block))
                held += block;
            return held;
        })).ToArray();

        start.Set();
        var heldByWorkers = await Task.WhenAll(tasks);

        Assert.IsTrue(budget.UsedBytes <= budget.CapacityBytes);
        Assert.IsTrue(budget.PeakUsedBytes <= budget.CapacityBytes);
        Assert.AreEqual(budget.UsedBytes, heldByWorkers.Sum());
        foreach (var held in heldByWorkers)
            if (held > 0)
                budget.Release(checked((int)held));
        Assert.AreEqual(0L, budget.UsedBytes);
    }

}
