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
    public void LargeCopyPoolIsReleasedBeforeVerifyStarts()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var releaseShared = engine.IndexOf("bufferPool?.Dispose();", StringComparison.Ordinal);
        var releaseIndependent = engine.IndexOf("foreach (var pool in independentPools) pool.Dispose();", StringComparison.Ordinal);
        var clearIndependent = engine.IndexOf("independentPools.Clear();", StringComparison.Ordinal);
        var verify = engine.IndexOf("if (options.Verify && !token.IsCancellationRequested)", StringComparison.Ordinal);
        Assert.IsTrue(releaseShared >= 0 && releaseIndependent > releaseShared &&
            clearIndependent > releaseIndependent && verify > clearIndependent);
        Assert.IsTrue(engine.LastIndexOf("foreach (var pool in independentPools) pool.Dispose();", StringComparison.Ordinal) > verify);
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
