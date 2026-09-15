using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SourceWindowPerformanceTests
{
    [TestMethod]
    public void TransferSizeRespondsToMemoryPressureAndDestinationCount()
    {
        var signals = new[] { new TransferDeviceSignal(8, 8, 0, 0) };

        var roomy = AdaptiveTransferSizer.Select(
            4L * 1024 * 1024 * 1024,
            activeDestinations: 1,
            requiredAlignment: 4096,
            bufferTargetBytes: 512L * 1024 * 1024,
            bufferUsedBytes: 0,
            currentPrefetchLimit: 4,
            signals);
        var pressured = AdaptiveTransferSizer.Select(
            4L * 1024 * 1024 * 1024,
            activeDestinations: 4,
            requiredAlignment: 4096,
            bufferTargetBytes: 512L * 1024 * 1024,
            bufferUsedBytes: 384L * 1024 * 1024,
            currentPrefetchLimit: 4,
            signals);

        Assert.IsGreaterThan(0, pressured);
        Assert.IsGreaterThan(4095, pressured);
        Assert.IsGreaterThan(pressured, roomy);
        Assert.AreEqual(0, roomy % 4096);
        Assert.AreEqual(0, pressured % 4096);
    }

    [TestMethod]
    public void MeasuredThroughputAndLatencyCanReduceOperationSize()
    {
        var noFeedback = new[] { new TransferDeviceSignal(16, 16, 0, 0) };
        var feedback = new[]
        {
            new TransferDeviceSignal(
                16,
                16,
                512d * 1024 * 1024,
                64d),
        };

        var initial = AdaptiveTransferSizer.Select(
            2L * 1024 * 1024 * 1024,
            1,
            4096,
            512L * 1024 * 1024,
            0,
            4,
            noFeedback);
        var measured = AdaptiveTransferSizer.Select(
            2L * 1024 * 1024 * 1024,
            1,
            4096,
            512L * 1024 * 1024,
            0,
            4,
            feedback);

        Assert.IsLessThan(initial, measured);
        Assert.AreEqual(0, measured % 4096);
    }

    [TestMethod]
    public void CopyEngineNoLongerExposesFixedReadBufferBands()
    {
        var method = typeof(CopyEngine).GetMethod(
            "ReadBufferSizeFor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNull(method);

        var fields = typeof(CopyEngine)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(fields, "BlockSize");
        CollectionAssert.DoesNotContain(fields, "SmallBufferSize");
        CollectionAssert.DoesNotContain(fields, "MediumBufferSize");
        CollectionAssert.DoesNotContain(fields, "LargeBufferSize");
    }

    [TestMethod]
    public void PipelineGovernorTracksChangedTransferSize()
    {
        const long oneGiB = 1024L * 1024 * 1024;
        var budget = new CopyEngine.AdaptiveByteBudget(512L * 1024 * 1024, oneGiB);
        var governor = new CopyEngine.PipelineGovernor(budget, 32 * 1024 * 1024);
        var before = governor.Snapshot().CurrentPrefetchLimit;

        governor.SetBytesPerBlock(8 * 1024 * 1024);
        for (var decision = 0; decision < 4; decision++)
        {
            for (var sample = 0; sample < 8; sample++)
                governor.RecordConsumerWait(TimeSpan.FromMilliseconds(10));
        }

        Assert.IsGreaterThanOrEqualTo(before, governor.Snapshot().CurrentPrefetchLimit);
    }
}
