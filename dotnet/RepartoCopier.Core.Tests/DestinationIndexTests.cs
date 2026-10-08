using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DestinationIndexTests
{
    [TestMethod]
    public void IndexFindsExistingFilesWithExactSizeAndTime()
    {
        using var temp = new TempScope();
        var nested = Directory.CreateDirectory(Path.Combine(temp.Path, "a", "b")).FullName;
        var file = Path.Combine(nested, "x.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var stamp = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        var index = DestinationIndex.Build(temp.Path, ["a", Path.Combine("a", "b")], CancellationToken.None);
        var existing = index.ExistingFile(Path.Combine("a", "b", "x.bin"));

        Assert.IsNotNull(existing);
        Assert.AreEqual(4L, existing.Length);
        Assert.AreEqual(stamp, existing.LastWriteTimeUtc);
        Assert.IsNull(index.ExistingFile(Path.Combine("a", "b", "missing.bin")));
        Assert.IsNull(index.ExistingFile("a"), "A folder is not an existing file.");
    }

    [TestMethod]
    public void AFileWhereTheSourceNeedsAFolderIsAConflict()
    {
        using var temp = new TempScope();
        File.WriteAllBytes(Path.Combine(temp.Path, "a"), [1]);

        var error = Assert.ThrowsExactly<IOException>(() =>
            DestinationIndex.Build(temp.Path, ["a"], CancellationToken.None));
        StringAssert.Contains(error.Message, "requiere una carpeta");
    }

    [TestMethod]
    public void AFolderWhereTheSourceNeedsAFileIsAConflict()
    {
        using var temp = new TempScope();
        Directory.CreateDirectory(Path.Combine(temp.Path, "x.bin"));
        var index = DestinationIndex.Build(temp.Path, [], CancellationToken.None);

        var error = Assert.ThrowsExactly<IOException>(() =>
            index.ValidateFiles([new ScannedFile(Path.Combine("src", "x.bin"), "x.bin", 1, DateTime.UtcNow)], CancellationToken.None));
        StringAssert.Contains(error.Message, "requiere un archivo");
    }

    [TestMethod]
    public void AMissingDestinationIsAnEmptyIndex()
    {
        using var temp = new TempScope();
        var index = DestinationIndex.Build(Path.Combine(temp.Path, "not-created"), ["a"], CancellationToken.None);
        Assert.IsNull(index.ExistingFile(Path.Combine("a", "x.bin")));
    }

    [TestMethod]
    public void MetadataOfAMissingFileIsAMismatchNotAnError()
    {
        using var temp = new TempScope();
        Assert.IsFalse(PreflightSafety.MatchesMetadata(Path.Combine(temp.Path, "gone.bin"), 1, DateTime.UtcNow));
    }

    [TestMethod]
    public async Task AStalePartFromAnInterruptedRunIsReplaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology.");
            return;
        }
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), [1, 2, 3]);
        var stamp = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);
        File.SetLastWriteTimeUtc(Path.Combine(source, "a.bin"), stamp);
        var root = Path.Combine(destination, "Source");
        Directory.CreateDirectory(root);
        var tmp = StateLayout.PrepareTempDirectory(root);
        var stale = StateLayout.PartPath(root, Path.Combine(root, "a.bin"));
        await File.WriteAllBytesAsync(stale, [9, 9, 9, 9, 9]);

        var plan = CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        await using var job = CopyEngine.Start(plan, new CopyOptions());
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(DestinationPhase.Done, job.Snapshot().Single().Phase);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "a.bin")));
        Assert.IsFalse(File.Exists(stale));
        // The time is set on the part's handle before its flush and survives the rename.
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(Path.Combine(root, "a.bin")));
        Assert.IsTrue(Directory.Exists(tmp));
    }

    private sealed class TempScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"destination-index-{Guid.NewGuid():N}");
        public TempScope() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
