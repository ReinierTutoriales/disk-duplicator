using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DiskRobustnessTests
{
    [TestMethod]
    public void NvmeKeepsItsProfileWhenTheMediaQueryDoesNotAnswer()
    {
        Assert.AreEqual(StorageProfileKind.Nvme, StorageIoProfile.For(Device("NVMe", StorageMediaKind.Unknown)).Kind);
        Assert.AreEqual(StorageProfileKind.Nvme, StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState)).Kind);
        // A rotational answer still wins: such a device is not treated as an NVMe SSD.
        Assert.AreEqual(StorageProfileKind.Rotational, StorageIoProfile.For(Device("NVMe", StorageMediaKind.Rotational)).Kind);
    }

    [TestMethod]
    public void CaseOnlyDuplicateNamesAreReportedBeforeCopying()
    {
        using var temp = new TempScope();
        File.WriteAllBytes(Path.Combine(temp.Path, "a.txt"), [1]);
        File.WriteAllBytes(Path.Combine(temp.Path, "A.txt"), [2]);
        if (Directory.GetFiles(temp.Path).Length < 2)
        {
            Assert.Inconclusive("This file system folds case: it cannot hold both names.");
            return;
        }
        var error = Assert.ThrowsExactly<IOException>(() => PreflightSafety.ScanDirectory(temp.Path));
        StringAssert.Contains(error.Message, "mayúsculas");
    }

    [TestMethod]
    public void AnUnreachableJournalNeverFailsTheDestinationOnDispose()
    {
        using var temp = new TempScope();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "dest", "Source")).FullName;
        StateLayout.PrepareTempDirectory(root);
        var writer = new RecoveryCheckpointWriter(root);
        writer.Append(new RecoveryFile(Path.Combine(temp.Path, "a.bin"), "a.bin", 1, 0), new byte[32]);
        // The destination disappears (unplugged) with a checkpoint still pending: its manifest can no longer be
        // flushed. Simulated with a stream that fails like a removed device does.
        var manifestField = typeof(RecoveryCheckpointWriter).GetField("_manifest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var real = (FileStream)manifestField.GetValue(writer)!;
        manifestField.SetValue(writer, new UnpluggedStream(Path.Combine(temp.Path, "unplugged.bin")));
        real.Dispose();

        writer.Dispose();
    }

    private sealed class UnpluggedStream(string path) : FileStream(path, FileMode.Create)
    {
        public override void Flush(bool flushToDisk)
        {
            if (flushToDisk)
                throw new IOException("El dispositivo no está conectado.", unchecked((int)0x8007048F));
        }
    }

    [TestMethod]
    public void ACommitWaitsOutABriefSharingLockFromAScanner()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows sharing semantics.");
            return;
        }
        using var temp = new TempScope();
        var part = Path.Combine(temp.Path, "a.part");
        var destination = Path.Combine(temp.Path, "a.bin");
        File.WriteAllBytes(part, [1, 2, 3]);
        var scanner = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Delay(700).ContinueWith(_ => scanner.Dispose(), TaskScheduler.Default);

        var watch = Stopwatch.StartNew();
        AtomicFileCommit.Commit(part, destination, Path.Combine(temp.Path, "a.bak"), allowReplace: false);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task AFileOpenForWritingByAnotherProgramIsStillCopied()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows sharing semantics.");
            return;
        }
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var log = Path.Combine(source, "app.log");
        await File.WriteAllBytesAsync(log, [1, 2, 3, 4]);
        // Like a log or mail store: kept open for writing, while nobody writes to it during the copy.
        using var writer = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var plan = CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        await using var job = CopyEngine.Start(plan, new CopyOptions());
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(DestinationPhase.Done, job.Snapshot().Single().Phase);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 },
            await File.ReadAllBytesAsync(Path.Combine(destination, "Source", "app.log")));
    }

    [TestMethod]
    public async Task FilesCopiedIntoACompressedFolderAreCompressed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires NTFS compression.");
            return;
        }
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(destination, "Source")).FullName;
        using (var compact = Process.Start(new ProcessStartInfo("compact.exe", $"/c \"{root}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            compact!.WaitForExit(30_000);
        }
        if ((File.GetAttributes(root) & FileAttributes.Compressed) == 0)
        {
            Assert.Inconclusive("This volume does not support NTFS compression.");
            return;
        }
        await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), new byte[256 * 1024]);

        var plan = CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        await using var job = CopyEngine.Start(plan, new CopyOptions());
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(DestinationPhase.Done, job.Snapshot().Single().Phase);
        var copied = Path.Combine(root, "data.bin");
        Assert.AreEqual(256L * 1024, new FileInfo(copied).Length);
        Assert.AreNotEqual(0, (int)(File.GetAttributes(copied) & FileAttributes.Compressed),
            "A file copied into a compressed folder must be compressed like the folder.");
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media) =>
        new(@"C:\dest", @"C:\", 1, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, null, 0);

    private sealed class TempScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"disk-robustness-{Guid.NewGuid():N}");
        public TempScope() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
