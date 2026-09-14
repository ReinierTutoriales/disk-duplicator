using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class AdaptiveSourcePipelineTests
{
    private const int BlockSize = 32 * 1024 * 1024;

    [TestMethod]
    public async Task ByteBudgetCanGrowBeyondFormerFourGiBCeiling()
    {
        const long eightGiB = 8L * 1024 * 1024 * 1024;
        var budget = new CopyEngine.AdaptiveByteBudget(512L * 1024 * 1024, eightGiB);
        const int blocks = 160; // 5 GiB at 32 MiB per block.

        Assert.AreEqual(256, budget.GetAdmissibleConcurrency(BlockSize));
        for (var index = 0; index < blocks; index++)
            await budget.AcquireAsync(BlockSize, CancellationToken.None);

        Assert.IsGreaterThan(4L * 1024 * 1024 * 1024, budget.UsedBytes);
        Assert.IsGreaterThan(4L * 1024 * 1024 * 1024, budget.TargetBytes);

        for (var index = 0; index < blocks; index++)
            budget.Release(BlockSize);
        Assert.AreEqual(0L, budget.UsedBytes);
    }

    [TestMethod]
    public void PipelineGovernorCanScaleBeyondFormerEightBlockCeiling()
    {
        const long eightGiB = 8L * 1024 * 1024 * 1024;
        var budget = new CopyEngine.AdaptiveByteBudget(512L * 1024 * 1024, eightGiB);
        var governor = new CopyEngine.PipelineGovernor(budget, BlockSize);

        // Eight consumer-wait samples trigger one decision. Repeated starvation
        // should grow 4 -> 8 -> 16 -> 32 while memory permits it.
        for (var round = 0; round < 3; round++)
        {
            for (var sample = 0; sample < 8; sample++)
                governor.RecordConsumerWait(TimeSpan.FromMilliseconds(10));
        }

        var snapshot = governor.Snapshot();
        Assert.IsGreaterThan(8, snapshot.CurrentPrefetchLimit);
        Assert.IsGreaterThan(8, snapshot.MaximumObservedPrefetchLimit);
        Assert.IsGreaterThanOrEqualTo(2, snapshot.Upshifts);
    }

    [TestMethod]
    public void FormerFixedSourcePipelineCeilingsStayRemoved()
    {
        var fields = typeof(CopyEngine)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();

        CollectionAssert.DoesNotContain(fields, "SourcePrefetchPhysicalCapacity");
        CollectionAssert.DoesNotContain(fields, "SourceHashPipelineCapacity");
        CollectionAssert.DoesNotContain(fields, "MaximumBufferBudget");
        CollectionAssert.DoesNotContain(fields, "MinimumBufferBudget");

        var budgetFields = typeof(CopyEngine.AdaptiveByteBudget)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(budgetFields, "_maximumBytes");
    }
}
