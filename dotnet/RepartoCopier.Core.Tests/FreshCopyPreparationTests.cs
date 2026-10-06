using Blake3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FreshCopyPreparationTests
{
    [TestMethod]
    public void FreshPreparationDoesNotOpenPreviousPayloadAndDoesNotTrustOldCheckpoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows file sharing semantics.");
            return;
        }
        using var temp = new TempScope();
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var destination = Path.Combine(root, "payload.bin");
        File.WriteAllBytes(destination, [1, 2, 3]);
        var file = new RecoveryFile(source, "payload.bin", 3, 0);
        using (var writer = new RecoveryCheckpointWriter(root))
            writer.Append(file, Hasher.Hash(new byte[] { 1, 2, 3 }).AsSpan());
        // Either attempted hash read would fail under these exclusive handles.
        using (var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var destinationLock = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var completed = RecoveryManager.PrepareAndNormalize(
                temp.Path, root, [file], reuseCompleted: false);
            Assert.AreEqual(0, completed.Count);
            Assert.AreEqual(0, RecoveryManager.LoadCompleted(root).Count);
        }
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [TestMethod]
    public void FreshPreparationStillRestoresOwnedBackupFromInterruptedCommit()
    {
        using var temp = new TempScope();
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var destination = Path.Combine(root, "payload.bin");
        var file = new RecoveryFile(source, "payload.bin", 3, 0);
        StateLayout.PrepareTempDirectory(root);
        var backup = StateLayout.BackupPath(root, destination);
        File.WriteAllBytes(backup, [4, 5, 6]);
        RecoveryManager.PrepareAndNormalize(temp.Path, root, [file], reuseCompleted: false);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
        Assert.IsFalse(File.Exists(backup));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RepeatedFreshCopyReallyWritesEveryFile(bool asyncStart, bool planSaysSkip)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var payload = new byte[3 * 1024 * 1024 + 137];
        new Random(71).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var plan = CopyPlan.Create(source, [destination], skipSame: planSaysSkip, keepGoing: false);
        var options = new CopyOptions(Verify: false, SkipSame: false);
        for (var run = 0; run < 2; run++)
        {
            await using var job = asyncStart
                ? await CopyEngine.StartAsync(plan, options)
                : CopyEngine.Start(plan, options);
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            var snapshot = job.Snapshot().Single();
            Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
            Assert.AreEqual(0UL, snapshot.FilesErrored);
            Assert.AreEqual(0UL, snapshot.FilesSkipped);
            Assert.AreEqual(1UL, snapshot.FilesDone);
            Assert.AreEqual((long)payload.Length, job.DiagnosticsSnapshot().SourceReadBytes);
            Assert.AreEqual((long)payload.Length, job.DiagnosticsSnapshot().WrittenBytes);
            Assert.AreEqual(1UL, snapshot.DurableFlushes);
            CollectionAssert.AreEqual(payload,
                await File.ReadAllBytesAsync(Path.Combine(destination, "Source", "payload.bin")));
        }
    }

    private sealed class TempScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"RepartoCopier-fresh-preparation-{Guid.NewGuid():N}");

        public TempScope() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
