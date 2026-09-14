using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SourceWindowPerformanceTests
{
    [TestMethod]
    public void LargeSourceReadsUseThirtyTwoMiBBlocks()
    {
        var method = typeof(CopyEngine).GetMethod(
            "ReadBufferSizeFor",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("No se encontró ReadBufferSizeFor.");

        var value = method.Invoke(null, [64L * 1024 * 1024]);

        Assert.AreEqual(32 * 1024 * 1024, (int)value!);
    }

    [TestMethod]
    public void PipelineGovernorStartsAtFourAndCanGrowToEight()
    {
        var governor = new CopyEngine.PipelineGovernor();
        Assert.AreEqual(4, governor.Snapshot().CurrentPrefetchLimit);

        for (var decision = 0; decision < 4; decision++)
        {
            for (var sample = 0; sample < 8; sample++)
                governor.RecordConsumerWait(TimeSpan.FromMilliseconds(10));
        }

        var snapshot = governor.Snapshot();
        Assert.AreEqual(8, snapshot.CurrentPrefetchLimit);
        Assert.AreEqual(8, snapshot.MaximumObservedPrefetchLimit);
        Assert.AreEqual(4, snapshot.Upshifts);
    }
}
