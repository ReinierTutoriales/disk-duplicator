using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationStreamingArchitectureTests
{
    [TestMethod]
    public void VerifyUsesFixedWorkspaceAndNoPerBlockHistory()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("VerificationWorkspaceBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlans", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationCrc32C", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyReadsSourceAndDestinationsAtSameOffsetAndComparesImmediately()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("entry.SourcePath", StringComparison.Ordinal));
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
