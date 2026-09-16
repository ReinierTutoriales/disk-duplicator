using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchPendingPayloadArchitectureTests
{
    [TestMethod]
    public void FanoutUsesOneSharedBlockAndOneQueuePerDestination()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.Channel.Writer.TryWrite(message)", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("Ingress", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("StageBranchAsync", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("DetachBranchBlock", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedPoolBackpressureReplacesReplayAndPrivateBranchBuffers()
    {
        var root = FindRepositoryRoot();
        var core = Path.Combine(root, "dotnet", "RepartoCopier.Core");
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayStore.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayPlacement.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchIsolationPolicy.cs")));
        var engine = File.ReadAllText(Path.Combine(core, "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("SharedFanoutPoolBytes", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("bufferBudget.AcquireAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SourcePrefetchGovernorAndAdaptiveTransferSizerAreRetired()
    {
        var root = FindRepositoryRoot();
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "AdaptiveTransferSizer.cs")));
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsFalse(engine.Contains("PipelineGovernor", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("ReadAndFanOutPrefetchedAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("SelectSharedFanoutBlockSize", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
