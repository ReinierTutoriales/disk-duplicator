using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationArchitectureTests
{
    [TestMethod]
    public void VerificationUsesDirectSequentialReadsWithFixedMemory()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("TryOpenOverlappedForVerification", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FileOptions.Asynchronous | FileOptions.SequentialScan", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("VerificationWorkspace", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "VerificationReadBudget.cs")));
    }

    [TestMethod]
    public void CompletedDestinationsVerifyBeforeSlowWritersDrain()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var verify = engine.IndexOf("await VerifyDestinationsAsync(", StringComparison.Ordinal);
        var release = engine.IndexOf("activeBufferPool.Dispose();", verify, StringComparison.Ordinal);
        Assert.IsTrue(verify >= 0 && release > verify);
        Assert.IsTrue(engine.Contains("Task.WhenAny(pendingWriters.Select(item => item.Task))", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("readySlots", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("IReadOnlyCollection<int> eligibleSlots", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("bufferPool?.Dispose();", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationCrc32C", StringComparison.Ordinal));
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
