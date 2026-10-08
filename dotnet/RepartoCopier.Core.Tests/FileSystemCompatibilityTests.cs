using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FileSystemCompatibilityTests
{
    [TestMethod]
    public void TimestampToleranceFollowsTheDestinationFileSystemPrecision()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(2), PreflightSafety.TimestampTolerance("FAT32"));
        Assert.AreEqual(TimeSpan.FromSeconds(2), PreflightSafety.TimestampTolerance("FAT"));
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), PreflightSafety.TimestampTolerance("exFAT"));
        Assert.AreEqual(TimeSpan.Zero, PreflightSafety.TimestampTolerance("NTFS"));
        Assert.AreEqual(TimeSpan.Zero, PreflightSafety.TimestampTolerance("ReFS"));
        Assert.AreEqual(TimeSpan.Zero, PreflightSafety.TimestampTolerance(null));
    }

    [TestMethod]
    public void MetadataMatchAcceptsOnlyTheFileSystemRounding()
    {
        using var temp = new TempScope();
        var target = Path.Combine(temp.Path, "rounded.bin");
        File.WriteAllBytes(target, [1, 2, 3]);
        var stored = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, stored);
        var source = stored.AddMilliseconds(-1500); // what FAT keeps of 03:04:04.5

        Assert.IsFalse(PreflightSafety.MatchesMetadata(target, 3, source));
        Assert.IsTrue(PreflightSafety.MatchesMetadata(target, 3, source, PreflightSafety.TimestampTolerance("FAT32")));
        Assert.IsFalse(PreflightSafety.MatchesMetadata(target, 3, stored.AddSeconds(-3), PreflightSafety.TimestampTolerance("FAT32")));
        Assert.IsFalse(PreflightSafety.MatchesMetadata(target, 4, source, PreflightSafety.TimestampTolerance("FAT32")));
    }

    [TestMethod]
    public void OnlyNameSurrogatesAndAppAliasesAreLinks()
    {
        Assert.IsTrue(WindowsPath.IsLinkTag(0xA000000C)); // symlink
        Assert.IsTrue(WindowsPath.IsLinkTag(0xA0000003)); // junction / mount point
        Assert.IsTrue(WindowsPath.IsLinkTag(0xA000001D)); // WSL symlink
        Assert.IsTrue(WindowsPath.IsLinkTag(0x8000001B)); // app execution alias
        Assert.IsFalse(WindowsPath.IsLinkTag(0x9000001A)); // OneDrive placeholder
        Assert.IsFalse(WindowsPath.IsLinkTag(0x9000601A)); // OneDrive placeholder (folder)
        Assert.IsFalse(WindowsPath.IsLinkTag(0x80000013)); // deduplication
        Assert.IsFalse(WindowsPath.IsLinkTag(0x80000017)); // WOF / CompactOS
    }

    [TestMethod]
    public void ExtendedPathsKeepLongPathsUsableByRawWin32Calls()
    {
        Assert.AreEqual(@"\\?\C:\a\b", WindowsPath.Extended(@"C:\a\b"));
        Assert.AreEqual(@"\\?\UNC\server\share\a", WindowsPath.Extended(@"\\server\share\a"));
        Assert.AreEqual(@"\\?\C:\a", WindowsPath.Extended(@"\\?\C:\a"));
        Assert.AreEqual(@"\\.\C:", WindowsPath.Extended(@"\\.\C:"));
    }

    [TestMethod]
    public void FilesOfFourGibibytesAreRejectedBeforeCopyingToFat32()
    {
        ScannedFile[] files = [new(@"C:\src\small.bin", "small.bin", 10, DateTime.UtcNow),
                               new(@"C:\src\big.iso", "big.iso", 4L * 1024 * 1024 * 1024, DateTime.UtcNow)];
        var error = Assert.ThrowsExactly<IOException>(() => PreflightSafety.RejectFilesTooLargeForFileSystem(
            [@"E:\Source", @"F:\Source"], [Device("NTFS"), Device("FAT32")], files));
        StringAssert.Contains(error.Message, @"F:\Source");
        StringAssert.Contains(error.Message, "big.iso");

        PreflightSafety.RejectFilesTooLargeForFileSystem([@"E:\Source", @"F:\Source"], [Device("NTFS"), Device("exFAT")], files);
        PreflightSafety.RejectFilesTooLargeForFileSystem([@"F:\Source"], [Device("FAT32")], [files[0]]);
    }

    [TestMethod]
    public async Task KeepGoingTurnsALockedSourceFileIntoOneFileError()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a-locked.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "b-free.bin"), [4, 5, 6]);
        using var exclusive = new FileStream(Path.Combine(source, "a-locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceMetadataDifferent, keepGoing: true);
        await using var job = CopyEngine.Start(plan, new CopyOptions(KeepGoing: true));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(1UL, snapshot.FilesErrored);
        Assert.IsFalse(File.Exists(Path.Combine(destination, "Source", "a-locked.bin")));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(destination, "Source", "b-free.bin")));
    }

    [TestMethod]
    public async Task WithoutKeepGoingALockedSourceFileStillFailsTheCopy()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a-locked.bin"), [1, 2, 3]);
        using var exclusive = new FileStream(Path.Combine(source, "a-locked.bin"), FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceMetadataDifferent, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions());
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(DestinationPhase.Failed, job.Snapshot().Single().Phase);
    }

    [TestMethod]
    public void FailedPreparationRemovesTheDestinationFoldersItCreated()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        File.WriteAllBytes(Path.Combine(source, "a.bin"), [1]);
        var first = Directory.CreateDirectory(Path.Combine(temp.Path, "One")).FullName;
        // The second destination resolves inside the first one's new "Source" folder: the overlap is only
        // detectable after both folders exist, so preparation fails after creating them.
        var nested = Path.Combine(first, "Source");

        var plan = CopyPlan.Create(source, [first, nested], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        Assert.ThrowsExactly<IOException>(() => CopyEngine.Start(plan));

        Assert.IsFalse(Directory.Exists(Path.Combine(first, "Source")));
        Assert.IsTrue(Directory.Exists(first));
    }

    private static StorageDeviceInfo Device(string fileSystem) =>
        new(@"E:\Source", @"E:\", 1, 1, "Usb", StorageMediaKind.SolidState, true, 512, 512, true, null, false, fileSystem);

    private static bool RequireWindows()
    {
        if (OperatingSystem.IsWindows()) return true;
        Assert.Inconclusive("Requires Windows file sharing semantics.");
        return false;
    }

    private sealed class TempScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"fs-compat-{Guid.NewGuid():N}");
        public TempScope() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
