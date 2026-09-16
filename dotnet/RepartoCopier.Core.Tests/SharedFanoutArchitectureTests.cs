using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutArchitectureTests
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
        })
        {
            Assert.IsFalse(product.Contains(token, StringComparison.Ordinal), $"Ruta retirada reapareció: {token}");
        }
    }

    [TestMethod]
    public void VerificationUsesCoordinatedDestinationReadsAgainstCopyTimeCrc()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FastCrc32C.Compute(target.Buffer!.Memory.Span[..block.Length])", StringComparison.Ordinal));
        var start = engine.IndexOf("private static async Task VerifyDestinationsAsync", StringComparison.Ordinal);
        var end = engine.IndexOf("private static async Task<bool[][]> BuildVerifiedSkipMasksAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var verify = engine[start..end];
        Assert.IsFalse(verify.Contains("entry.SourcePath", StringComparison.Ordinal), "Verify no debe releer el origen.");
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
