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
        const string release = "DisposePools(bufferPool, independentPools);";
        var releasePools = engine.IndexOf(release, StringComparison.Ordinal);
        var clearIndependent = engine.IndexOf("independentPools.Clear();", StringComparison.Ordinal);
        var verify = engine.IndexOf("if (options.Verify && !token.IsCancellationRequested)", StringComparison.Ordinal);
        Assert.IsTrue(releasePools >= 0 && clearIndependent > releasePools && verify > clearIndependent);
        // The final cleanup releases them again in case COPY failed before the release above.
        Assert.IsTrue(engine.LastIndexOf(release, StringComparison.Ordinal) > verify);
        var helper = engine[engine.IndexOf("private static void DisposePools(", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("private static void DrainAndRelease(", StringComparison.Ordinal)];
        Assert.IsTrue(helper.Contains("shared?.Dispose();", StringComparison.Ordinal));
        Assert.IsTrue(helper.Contains("foreach (var pool in independent)", StringComparison.Ordinal));
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
