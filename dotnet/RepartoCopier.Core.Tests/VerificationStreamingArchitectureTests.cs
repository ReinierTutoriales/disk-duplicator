using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationStreamingArchitectureTests
{
    [TestMethod]
    public void VerifyUsesOneFixedContiguousWorkspaceAndNoPerTargetRentals()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("VerificationWorkspaceBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("sealed class VerificationWorkspace", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("GC.AllocateUninitializedArray<byte>", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("SourceBufferLease.BorrowPinned", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("targets[index].Open(workspace.RentSlice(index))", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlans", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyReadsSourceAndDestinationsTogetherAndComparesImmediately()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("RecordVerifyLogicalBytes(expectedBytes)", StringComparison.Ordinal));
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
