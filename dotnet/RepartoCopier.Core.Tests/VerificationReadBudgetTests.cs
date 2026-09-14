using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationReadBudgetTests
{
    [TestMethod]
    public async Task ByteBudgetAllowsMoreThanTwoConcurrentReadsWhenMemoryPermits()
    {
        const int slice = 16 * 1024 * 1024;
        var budget = new VerificationReadBudget(4L * slice);

        using var first = await budget.AcquireAsync(slice, CancellationToken.None);
        using var second = await budget.AcquireAsync(slice, CancellationToken.None);
        using var third = await budget.AcquireAsync(slice, CancellationToken.None);
        using var fourth = await budget.AcquireAsync(slice, CancellationToken.None);

        Assert.AreEqual(4L * slice, budget.UsedBytes);
        Assert.AreEqual(4L * slice, budget.PeakUsedBytes);
        Assert.AreEqual(4L * slice, budget.LimitBytes);
    }

    [TestMethod]
    public async Task ByteBudgetBlocksOnlyOnMemoryBytesNotFixedQueueDepth()
    {
        const int slice = 32 * 1024 * 1024;
        var budget = new VerificationReadBudget(2L * slice);

        var first = await budget.AcquireAsync(slice, CancellationToken.None);
        using var second = await budget.AcquireAsync(slice, CancellationToken.None);
        var thirdTask = budget.AcquireAsync(slice, CancellationToken.None).AsTask();

        await Task.Delay(25);
        Assert.IsFalse(thirdTask.IsCompleted);
        first.Dispose();

        using var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2L * slice, budget.UsedBytes);
        Assert.AreEqual(2L * slice, budget.PeakUsedBytes);
    }

    [TestMethod]
    public async Task OversizedSingleReadCanMakeForwardProgressWithoutDeadlock()
    {
        const int request = 64 * 1024 * 1024;
        var budget = new VerificationReadBudget(32L * 1024 * 1024);

        using var lease = await budget.AcquireAsync(request, CancellationToken.None);

        Assert.AreEqual((long)request, budget.UsedBytes);
        Assert.AreEqual((long)request, budget.PeakUsedBytes);
    }

    [TestMethod]
    public async Task CancelledWaiterDoesNotLeakVerificationMemory()
    {
        const int slice = 8 * 1024 * 1024;
        var budget = new VerificationReadBudget(slice);
        using var first = await budget.AcquireAsync(slice, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var blocked = budget.AcquireAsync(slice, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);
        Assert.AreEqual((long)slice, budget.UsedBytes);
    }
}
