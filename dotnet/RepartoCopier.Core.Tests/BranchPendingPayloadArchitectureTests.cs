using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchPendingPayloadArchitectureTests
{
    [TestMethod]
    public void IsolationThresholdTracksPhysicalQueueWindow()
    {
        Assert.AreEqual(32L * 1024 * 1024, BranchIsolationPolicy.SharedRetentionTargetBytes(4 * 1024 * 1024, 8, 256L * 1024 * 1024));
        Assert.AreEqual(256L * 1024 * 1024, BranchIsolationPolicy.SharedRetentionTargetBytes(8 * 1024 * 1024, 64, 256L * 1024 * 1024));
        Assert.IsFalse(BranchIsolationPolicy.ShouldDetach(32L * 1024 * 1024, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));
        Assert.IsTrue(BranchIsolationPolicy.ShouldDetach(36L * 1024 * 1024, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));
    }

    [TestMethod]
    public void ProducerHotPathDoesNotAwaitReplayIo()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var deliverStart = engine.IndexOf("private static async Task DeliverDataAsync", StringComparison.Ordinal);
        var stageStart = engine.IndexOf("private static async Task StageBranchAsync", StringComparison.Ordinal);
        Assert.IsTrue(deliverStart >= 0 && stageStart > deliverStart);
        var producerDelivery = engine[deliverStart..stageStart];
        Assert.IsFalse(producerDelivery.Contains("SpillAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine[stageStart..].Contains("ReplayStore.SpillAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PhysicalBacklogIsReleasedWithCompletedBranchPayload()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var releaseStart = engine.IndexOf("private static void ReleaseBranchPayload", StringComparison.Ordinal);
        Assert.IsTrue(releaseStart >= 0);
        var tail = engine[releaseStart..Math.Min(engine.Length, releaseStart + 500)];
        Assert.IsTrue(tail.Contains("ReleasePendingPayload", StringComparison.Ordinal));
        Assert.IsTrue(tail.Contains("DeviceScheduler.ReleaseBacklog", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PendingWritesHaveAdaptiveAdmissionWindow()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("EnsureWriteWindowAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("DeviceScheduler.ExplorationQueueDepth", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SupersededTimedReplayGateCannotReturn()
    {
        var root = FindRepositoryRoot();
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "BranchReplayGate.cs")));

        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsFalse(engine.Contains("ReplayGate", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OneDestinationQueueDepthCannotShrinkGlobalSourceBlockSize()
    {
        var root = FindRepositoryRoot();
        var sizer = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "AdaptiveTransferSizer.cs"));
        Assert.IsFalse(sizer.Contains("devices.Max", StringComparison.Ordinal));
        Assert.IsTrue(sizer.Contains("largestMeasuredBytesPerOperation", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
