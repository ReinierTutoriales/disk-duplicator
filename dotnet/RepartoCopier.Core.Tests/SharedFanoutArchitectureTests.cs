using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutArchitectureTests
{
    [TestMethod]
    public void FanoutUsesOneSharedBlockAndOneQueuePerDestination()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("new SharedBlock(lease, read)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.Channel.Writer.TryWrite(message)", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("Ingress", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("StageBranchAsync", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("DetachBranchBlock", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BoundedPrivateSpillReplacesReplayWithoutReintroducingGlobalBackpressure()
    {
        var root = FindRepositoryRoot();
        var core = Path.Combine(root, "dotnet", "RepartoCopier.Core");
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayStore.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayPlacement.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchIsolationPolicy.cs")));
        var engine = File.ReadAllText(Path.Combine(core, "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("SharedFanoutPoolBytes", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("bufferPool.RentAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FanoutSpillBlock.CopyFrom", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("Math.Max(1, normal.Count)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.TryReserveSpill(read)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FanoutSpillPartitioner.Partition", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains(".ShouldSpill(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RetiredAdaptiveAndReplayPathsCannotReturn()
    {
        var root = FindRepositoryRoot();
        var core = Path.Combine(root, "dotnet", "RepartoCopier.Core");
        var retiredFiles = new[]
        {
            "AdaptiveTransferSizer.cs",
            "BranchFlowSnapshot.cs",
            "BranchIsolationPolicy.cs",
            "BranchReplayPlacement.cs",
            "BranchReplayStore.cs",
            "FastVerificationReader.cs",
            "VerificationReadBudget.cs",
        };
        foreach (var file in retiredFiles)
            Assert.IsFalse(File.Exists(Path.Combine(core, file)), $"No debe reaparecer {file}.");

        var product = Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Aggregate(string.Empty, static (all, next) => all + "\n" + next);
        foreach (var token in new[]
        {
            "PipelineGovernor",
            "AdaptiveTransferSizer",
            "BranchReplayStore",
            "BranchReplayPlacement",
            "BranchIsolationPolicy",
            "ReplayDataMessage",
            "StageBranchAsync",
            "DetachBranchBlock",
            "EnableReplay",
            "QueueDepthUpshifts++",
            "QueueDepthDownshifts++",
        })
        {
            Assert.IsFalse(product.Contains(token, StringComparison.Ordinal), $"Ruta retirada reapareció: {token}");
        }
    }

    [TestMethod]
    public void VerificationUsesStreamingSourceAndDestinationCrcComparison()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));
    }


    [TestMethod]
    public void CompletedFanoutMustReleaseEveryMemoryAndBacklogCounter()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("ValidateFanoutDrain(workers, spillBudget, activeBufferPool)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.PendingPayloadBytes != 0", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.SpillBytes != 0", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.DeviceScheduler.QueuedBytes != 0", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("spillBudget.UsedBytes != 0", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("bufferPool.UsedBytes != 0", StringComparison.Ordinal));
    }


    [TestMethod]
    public void PrivateSpillBlockOwnsItsReservationUntilRelease()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("new SpillBlock(worker, privateBlock)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("Interlocked.Exchange(ref _owner, null)?.ReleaseSpill(length)", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("if (spill) worker.ReleaseSpill(length)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ActiveDestinationCountUsesAtomicLifecycleNotHotPathEnumeration()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Volatile.Read(ref activeDestinationCount)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("Interlocked.Decrement(ref activeDestinationCount)", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("workers.Count(worker => worker.IsActive)", StringComparison.Ordinal));
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
