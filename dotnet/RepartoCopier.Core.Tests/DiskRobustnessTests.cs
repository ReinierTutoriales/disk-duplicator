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
    [DataRow("_journal")]
    [DataRow("_manifest")]
    public void RecoveryCloseFailureStillClosesBothStreamsAndDoesNotEscape(string fieldName)
    {
        using var temp = new TempScope();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "dest", "Source")).FullName;
        var writer = new RecoveryCheckpointWriter(root);
        writer.Append(new RecoveryFile(Path.Combine(temp.Path, "a.bin"), "a.bin", 1, 0), new byte[32]);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var journalField = typeof(RecoveryCheckpointWriter).GetField("_journal", flags)!;
        var manifestField = typeof(RecoveryCheckpointWriter).GetField("_manifest", flags)!;
        using var journal = (FileStream)journalField.GetValue(writer)!;
        using var manifest = (FileStream)manifestField.GetValue(writer)!;
        var replacedField = typeof(RecoveryCheckpointWriter).GetField(fieldName, flags)!;
        ((FileStream)replacedField.GetValue(writer)!).Dispose();
        replacedField.SetValue(writer, new CloseFailureStream(Path.Combine(temp.Path, "unplugged.bin")));
        var journalHandle = ((FileStream)journalField.GetValue(writer)!).SafeFileHandle;
        var manifestHandle = ((FileStream)manifestField.GetValue(writer)!).SafeFileHandle;

        writer.Dispose();
        Assert.IsTrue(journalHandle.IsClosed);
        Assert.IsTrue(manifestHandle.IsClosed);
        writer.Dispose(); // Cleanup remains idempotent after an I/O failure.
    }

    private sealed class CloseFailureStream(string path) : FileStream(path, FileMode.Create)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw new IOException("El dispositivo desapareció al cerrar.", unchecked((int)0x8007048F));
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

    [TestMethod]
    public void FailCurrentFileAfterWatchdogFailureCleansUpWithoutDoubleCountingOrOverwritingError()
    {
        using var temp = new TempScope();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "dest", "Source")).FullName;
        var partPath = Path.Combine(root, "a.bin.reparto-part");
        File.WriteAllBytes(partPath, new byte[4096]);
        using var partStream = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var engineType = typeof(CopyEngine);
        var workerType = engineType.GetNestedType("DestinationWorker", System.Reflection.BindingFlags.NonPublic)!;
        var currentFileType = engineType.GetNestedType("CurrentFile", System.Reflection.BindingFlags.NonPublic)!;
        var entryType = engineType.GetNestedType("FileEntry", System.Reflection.BindingFlags.NonPublic)!;
        var failCurrentFile = engineType.GetMethod("FailCurrentFile", flags)!;

        var progress = new DestinationProgress("dest", 4096, 1);
        using var scheduler = new DeviceScheduler("disk-1", 1, 1024 * 1024);
        var budget = new AdaptiveControlByteBudget(_ => 1024 * 1024);
        var worker = Activator.CreateInstance(
            workerType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            [root, 0, progress, Device("USB", StorageMediaKind.SolidState), scheduler, budget],
            null)!;

        var entry = Activator.CreateInstance(
            entryType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            [Path.Combine(temp.Path, "a.bin"), "a.bin", 4096L, DateTime.UtcNow, 0L],
            null)!;
        var current = Activator.CreateInstance(
            currentFileType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            [entry, Path.Combine(root, "a.bin"), partPath, Path.Combine(root, "a.bin.reparto-bak"), partStream, null, false],
            null)!;

        const string watchdogMessage = "El destino dejó de responder durante la escritura.";
        workerType.GetMethod("Fail")!.Invoke(worker, [watchdogMessage]);
        failCurrentFile.Invoke(null, [worker, current, new CopyOptions(KeepGoing: true), "a.bin: error 995"]);

        var snapshot = progress.Snapshot();
        Assert.AreEqual(1UL, snapshot.FilesErrored);
        Assert.AreEqual(DestinationPhase.Failed, snapshot.Phase);
        Assert.AreEqual(watchdogMessage, snapshot.Error);
        Assert.IsFalse(File.Exists(partPath), "Cleanup must still delete the orphaned .part file.");
        Assert.IsTrue(partStream.SafeFileHandle.IsClosed, "Cleanup must still close the open part stream.");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "Direct I/O")]
    [DataRow(false, DisplayName = "Buffered")]
    public async Task SourceTruncatedAfterScanThrowsSourceChangedException(bool directIo)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows.");
            return;
        }
        using var temp = new TempScope();
        var sourcePath = Path.Combine(temp.Path, "truncated.bin");
        const int actualLength = 65536 + 512;
        const long expectedEntrySize = 4L * 1024 * 1024;
        await File.WriteAllBytesAsync(sourcePath, new byte[actualLength]);
        // A device whose probe failed is never eligible for Direct I/O, which forces the buffered path.
        var sourceDevice = directIo
            ? Device("NVMe", StorageMediaKind.SolidState)
            : Device("NVMe", StorageMediaKind.SolidState) with { ProbeSucceeded = false };
        if (DirectIoSourceReader.TryOpenOverlapped(sourcePath, sourceDevice, 1024 * 1024, out var probe))
            probe!.Dispose();
        else if (directIo)
        {
            Assert.Inconclusive("Volume does not support unbuffered Direct I/O.");
            return;
        }

        var engineType = typeof(CopyEngine);
        var workerType = engineType.GetNestedType("DestinationWorker", System.Reflection.BindingFlags.NonPublic)!;
        var entryType = engineType.GetNestedType("FileEntry", System.Reflection.BindingFlags.NonPublic)!;
        var sourceChangedType = engineType.GetNestedType("SourceChangedException", System.Reflection.BindingFlags.NonPublic)!;
        var readMethod = engineType.GetMethod("ReadAndFanOutSequentialAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        var progress = new DestinationProgress("dest", (ulong)expectedEntrySize, 1);
        using var scheduler = new DeviceScheduler("disk-1", 1, 4 * 1024 * 1024);
        var budget = new AdaptiveControlByteBudget(_ => 4 * 1024 * 1024);
        var worker = Activator.CreateInstance(
            workerType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            [Path.Combine(temp.Path, "dest"), 0, progress, Device("NVMe", StorageMediaKind.SolidState), scheduler, budget],
            null)!;
        var activeListType = typeof(List<>).MakeGenericType(workerType);
        var active = Activator.CreateInstance(activeListType)!;
        activeListType.GetMethod("Add")!.Invoke(active, [worker]);

        var entry = Activator.CreateInstance(
            entryType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null,
            [sourcePath, "truncated.bin", expectedEntrySize, DateTime.UtcNow, 0L],
            null)!;

        using var pool = new SharedFanoutBufferPool(4 * 1024 * 1024, 65536);
        await using var job = new CopyJob([progress]);

        var task = (Task)readMethod.Invoke(
            null,
            [entry, sourceDevice, active, 1024 * 1024, 4096, pool, job, null, false])!;
        var ex = await Assert.ThrowsAsync<IOException>(async () => await task);
        Assert.AreEqual(sourceChangedType, ex.GetType());
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
