using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationArchitectureTests
{
    [TestMethod]
    public void VerificationAdvancesSourceAndAllDestinationsTogetherByChunk()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("ReadVerifyTargetAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "VerificationReadBudget.cs")));
    }

    [TestMethod]
    public void VerificationUsesFixedWorkspaceAndNoCopyTimeCrcHistory()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var start = engine.IndexOf("private static async Task VerifyDestinationsAsync", StringComparison.Ordinal);
        var end = engine.IndexOf("private static async Task<bool[][]> BuildVerifiedSkipMasksAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var verify = engine[start..end];
        Assert.IsTrue(verify.Contains("entry.SourcePath", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("VerificationWorkspaceBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationCrc32C", StringComparison.Ordinal));
        Assert.IsFalse(verify.Contains("PendingRead", StringComparison.Ordinal));
        Assert.IsFalse(verify.Contains("ExplorationQueueDepth", StringComparison.Ordinal));
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
