using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutPoolArchitectureTests
{
    [TestMethod]
    public void ProductiveFanoutUsesSingle256MiBPageReferencedPool()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var pool = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "SharedFanoutBufferPool.cs"));

        StringAssert.Contains(engine, "SharedFanoutPoolBytes = 256L * 1024 * 1024");
        StringAssert.Contains(engine, "new SharedFanoutBufferPool");
        StringAssert.Contains(engine, "SharedFanoutBlockBytes = 8 * 1024 * 1024");
        Assert.IsFalse(engine.Contains("AdaptiveByteBudget", StringComparison.Ordinal));
        StringAssert.Contains(engine, "bufferPool.RentAsync");
        StringAssert.Contains(pool, "GC.AllocateUninitializedArray<byte>");
        StringAssert.Contains(pool, "pinned: true");
        StringAssert.Contains(pool, "VirtualLock");
        StringAssert.Contains(pool, "_pageReferences");
        StringAssert.Contains(pool, "ReleaseReference");
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
        throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
