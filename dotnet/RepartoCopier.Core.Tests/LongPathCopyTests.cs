using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class LongPathCopyTests
{
    [TestMethod]
    public async Task FanoutCopyPreservesLongPathsEmptyDirectoriesAndReplacementCommit()
    {
        using var temp = new TempDirectory();
        var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "OrigenLargo")).FullName;
        var nested = sourceRoot;
        while (Path.Combine(nested, "payload.bin").Length < 320)
        {
            nested = Path.Combine(nested, "segmento-0123456789abcdef");
            Directory.CreateDirectory(nested);
        }

        var emptyDirectory = Path.Combine(nested, "carpeta-vacia");
        Directory.CreateDirectory(emptyDirectory);
        var sourceFile = Path.Combine(nested, "payload.bin");
        var firstPayload = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourceFile, firstPayload);

        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "destino")).FullName;
        var plan = CopyPlan.Create(sourceRoot, [destinationBase], skipSame: false, keepGoing: false);

        await RunAndAssertSuccessAsync(plan);

        var copiedRoot = Path.Combine(destinationBase, Path.GetFileName(sourceRoot));
        var relativeNested = Path.GetRelativePath(sourceRoot, nested);
        var copiedFile = Path.Combine(copiedRoot, relativeNested, "payload.bin");
        var copiedEmptyDirectory = Path.Combine(copiedRoot, relativeNested, "carpeta-vacia");

        Assert.IsTrue(copiedFile.Length > 260, $"La ruta final debe superar MAX_PATH: {copiedFile.Length}");
        CollectionAssert.AreEqual(firstPayload, await File.ReadAllBytesAsync(copiedFile));
        Assert.IsTrue(Directory.Exists(copiedEmptyDirectory), "La carpeta vacía debe conservarse.");

        // Force a second transactional replacement through .part -> durable flush -> commit.
        var secondPayload = firstPayload.Select(value => (byte)(value ^ 0x5A)).ToArray();
        await File.WriteAllBytesAsync(sourceFile, secondPayload);
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow.AddSeconds(2));

        await RunAndAssertSuccessAsync(plan);

        CollectionAssert.AreEqual(secondPayload, await File.ReadAllBytesAsync(copiedFile));
        Assert.IsTrue(Directory.Exists(copiedEmptyDirectory), "La carpeta vacía debe sobrevivir al reemplazo.");

        var stateDirectory = StateLayout.StateDirectoryFor(copiedRoot);
        Assert.IsTrue(Directory.Exists(stateDirectory), "El estado transaccional debe permanecer fuera del árbol copiado.");
        Assert.IsFalse(
            stateDirectory.StartsWith(copiedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "El estado no debe quedar dentro de la carpeta replicada.");
    }

    private static async Task RunAndAssertSuccessAsync(CopyPlan plan)
    {
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repartocopier-long-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
