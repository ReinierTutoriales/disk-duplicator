using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class AdaptiveQueueDepthArchitectureTests
{
    [TestMethod]
    public void FixedQueueDepthArchitectureStaysRemoved()
    {
        var schedulerFields = typeof(DeviceScheduler)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerFields, "_ioSlots");

        var schedulerProperties = typeof(DeviceScheduler)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerProperties, "MaxOutstandingIo");
        CollectionAssert.Contains(schedulerProperties, "CurrentQueueDepth");
        CollectionAssert.Contains(schedulerProperties, "ExplorationQueueDepth");

        var profileProperties = typeof(StorageIoProfile)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(profileProperties, "RecommendedQueueDepth");
        CollectionAssert.Contains(profileProperties, "InitialQueueDepth");
    }

    [TestMethod]
    public void QueueDepthFeedbackTracksObservedConcurrencyWave()
    {
        var schedulerFields = typeof(DeviceScheduler)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .ToArray();

        CollectionAssert.Contains(schedulerFields, "_samplePeakObservedConcurrency");
        CollectionAssert.Contains(schedulerFields, "_sampleCompletions");
        CollectionAssert.Contains(schedulerFields, "_sampleSawDemand");
    }

    [TestMethod]
    public void ExplorationDepthIsNotLegacyHardwareClassCap()
    {
        using var nvme = new DeviceScheduler("NVMe-test", 16, 512L * 1024 * 1024);
        using var sata = new DeviceScheduler("SATA-test", 8, 256L * 1024 * 1024);
        using var usb = new DeviceScheduler("USB-test", 4, 256L * 1024 * 1024);

        Assert.AreEqual(32, nvme.ExplorationQueueDepth);
        Assert.AreEqual(16, sata.ExplorationQueueDepth);
        Assert.AreEqual(8, usb.ExplorationQueueDepth);
    }

    [TestMethod]
    public void DiagnosticsExposeAdaptiveDecisionAndMeasuredBestPoint()
    {
        using var scheduler = new DeviceScheduler("telemetry-test", 16, 512L * 1024 * 1024);
        var snapshot = scheduler.Snapshot();

        Assert.AreEqual("initial", snapshot.LastQueueDepthDecision);
        Assert.AreEqual(0d, snapshot.BestObservedThroughputBytesPerSecond);
        Assert.AreEqual(0d, snapshot.BestObservedAverageLatencyMilliseconds);
        Assert.AreEqual(16, snapshot.BestObservedQueueDepth);
    }
}
