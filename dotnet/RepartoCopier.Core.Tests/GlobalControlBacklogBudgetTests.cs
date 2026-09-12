using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class GlobalControlBacklogBudgetTests
{
    [TestMethod]
    public async Task BudgetBlocksGloballyUntilAnyQueuedMessageIsReleased()
    {
        var budget = new GlobalControlBacklogBudget(2);

        await budget.AcquireAsync(CancellationToken.None);
        await budget.AcquireAsync(CancellationToken.None);
        var third = budget.AcquireAsync(CancellationToken.None).AsTask();

        Assert.AreEqual(2, budget.Used);
        Assert.AreEqual(2, budget.Peak);
        Assert.IsFalse(third.IsCompleted);

        budget.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, budget.Used);
        Assert.AreEqual(2, budget.Peak);

        budget.Release();
        budget.Release();
        Assert.AreEqual(0, budget.Used);
    }

    [TestMethod]
    public async Task CancelledWaiterDoesNotLeakAControlSlot()
    {
        var budget = new GlobalControlBacklogBudget(1);
        await budget.AcquireAsync(CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(cancel.Token).AsTask();
        cancel.Cancel();

        try
        {
            await waiting;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(1, budget.Used);

        budget.Release();
        Assert.AreEqual(0, budget.Used);

        await budget.AcquireAsync(CancellationToken.None);
        Assert.AreEqual(1, budget.Used);
        budget.Release();
    }

    [TestMethod]
    public void ReleasingWithoutOwnershipIsRejected()
    {
        var budget = new GlobalControlBacklogBudget(4);
        Assert.ThrowsExactly<InvalidOperationException>(budget.Release);
    }

    [TestMethod]
    public async Task CancelledHeadWaiterDoesNotBlockFollowingWaiters()
    {
        var budget = new GlobalControlBacklogBudget(1);
        await budget.AcquireAsync(CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var cancelled = budget.AcquireAsync(cancel.Token).AsTask();
        var follower = budget.AcquireAsync(CancellationToken.None).AsTask();
        cancel.Cancel();

        try
        {
            await cancelled;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.IsFalse(follower.IsCompleted);

        budget.Release();
        await follower.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, budget.Used);

        budget.Release();
        Assert.AreEqual(0, budget.Used);
    }
}
