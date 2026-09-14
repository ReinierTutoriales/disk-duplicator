using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class AdaptiveControlByteBudgetTests
{
    [TestMethod]
    public async Task LargeControlBacklogDoesNotThrottleWhileByteHeadroomExists()
    {
        var bytes = AdaptiveControlByteBudget.EstimatedDeliveryBytes;
        var capacity = checked(50_000L * bytes + bytes);
        var budget = new AdaptiveControlByteBudget(_ => capacity);

        for (var index = 0; index < 50_000; index++)
            await budget.AcquireAsync(bytes, CancellationToken.None);

        Assert.AreEqual(50_000L * bytes, budget.UsedBytes);
        Assert.AreEqual(50_000L * bytes, budget.PeakBytes);

        for (var index = 0; index < 50_000; index++)
            budget.Release(bytes);

        Assert.AreEqual(0, budget.UsedBytes);
    }

    [TestMethod]
    public async Task PressureBlocksOnlyUntilRealByteCapacityReturns()
    {
        var bytes = AdaptiveControlByteBudget.EstimatedDeliveryBytes;
        long capacity = bytes;
        var budget = new AdaptiveControlByteBudget(_ => Volatile.Read(ref capacity));

        await budget.AcquireAsync(bytes, CancellationToken.None);
        var pending = budget.AcquireAsync(bytes, CancellationToken.None).AsTask();
        Assert.IsFalse(pending.IsCompleted);

        Volatile.Write(ref capacity, 2L * bytes);
        budget.Release(bytes);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(bytes, budget.UsedBytes);
        budget.Release(bytes);
        Assert.AreEqual(0, budget.UsedBytes);
    }

    [TestMethod]
    public async Task CancelledPressureWaiterDoesNotLeakReservedBytes()
    {
        var bytes = AdaptiveControlByteBudget.EstimatedDeliveryBytes;
        var budget = new AdaptiveControlByteBudget(_ => bytes);
        await budget.AcquireAsync(bytes, CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var pending = budget.AcquireAsync(bytes, cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await pending);

        Assert.AreEqual(bytes, budget.UsedBytes);
        budget.Release(bytes);
        Assert.AreEqual(0, budget.UsedBytes);
    }
}
