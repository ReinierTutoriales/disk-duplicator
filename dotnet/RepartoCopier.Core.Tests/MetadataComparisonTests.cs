using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class MetadataComparisonTests
{
    [TestMethod]
    public void MetadataCheckDoesNotOpenPayloadAndRequiresExactSizeAndUtcTime()
    {
        using var temp = new TempScope();
        var target = Path.Combine(temp.Path, "locked.bin");
        File.WriteAllBytes(target, [9, 9, 9]);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, stamp);
        using var exclusive = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.IsTrue(PreflightSafety.MatchesMetadata(target, 3, stamp));
        Assert.IsFalse(PreflightSafety.MatchesMetadata(target, 4, stamp));
        Assert.IsFalse(PreflightSafety.MatchesMetadata(target, 3, stamp.AddSeconds(1)));
    }

    [TestMethod]
    public async Task FastModeSkipsMetadataMatchesWithoutComparingTheirContent()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var sourceFile = Path.Combine(source, "a.bin");
        var existing = Path.Combine(targetRoot, "a.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3]);
        await File.WriteAllBytesAsync(existing, [9, 9, 9]);
        File.SetLastWriteTimeUtc(sourceFile, stamp);
        File.SetLastWriteTimeUtc(existing, stamp);

        // Deliberately different bytes with matching metadata document the heuristic's limitation.
        // An exclusive handle also proves neither comparison nor recovery reads the destination payload.
        using var exclusive = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.None);
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(1UL, snapshot.FilesSkipped);
        Assert.AreEqual(0UL, snapshot.FilesErrored);
        Assert.AreEqual(0UL, snapshot.ComparisonBytesRead);
        Assert.AreEqual(0UL, snapshot.ComparisonIdenticalFiles);
        Assert.AreEqual(0UL, snapshot.VerifiedBytes);
        Assert.AreEqual(0L, job.DiagnosticsSnapshot().SourceReadBytes);
        exclusive.Dispose();
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(existing));
    }

    [TestMethod]
    public async Task FastModeReplacesDifferentSizeOrDateAndCopiesMissingFiles()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (var name in new[] { "size.bin", "date.bin", "missing.bin" })
        {
            var path = Path.Combine(source, name);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, stamp);
        }
        await File.WriteAllBytesAsync(Path.Combine(targetRoot, "size.bin"), [9]);
        File.SetLastWriteTimeUtc(Path.Combine(targetRoot, "size.bin"), stamp);
        await File.WriteAllBytesAsync(Path.Combine(targetRoot, "date.bin"), [9, 9, 9]);
        File.SetLastWriteTimeUtc(Path.Combine(targetRoot, "date.bin"), stamp.AddMinutes(-1));
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceMetadataDifferent, false);

        await using (var job = CopyEngine.Start(plan, new CopyOptions(Verify: false)))
        {
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            var snapshot = job.Snapshot().Single();
            Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
            Assert.AreEqual(0UL, snapshot.FilesSkipped);
            Assert.AreEqual(0UL, snapshot.FilesErrored);
            Assert.AreEqual(0UL, snapshot.ComparisonBytesRead);
        }
        foreach (var name in new[] { "size.bin", "date.bin", "missing.bin" })
        {
            var path = Path.Combine(targetRoot, name);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path));
        }

        // The next run must omit all files just copied, with no content comparison or verification.
        await using var repeated = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await repeated.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        var repeatedSnapshot = repeated.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, repeatedSnapshot.Phase);
        Assert.AreEqual(3UL, repeatedSnapshot.FilesSkipped);
        Assert.AreEqual(0UL, repeatedSnapshot.ComparisonBytesRead);
        Assert.AreEqual(0UL, repeatedSnapshot.VerifiedBytes);
        Assert.AreEqual(0L, repeated.DiagnosticsSnapshot().SourceReadBytes);
    }

    [TestMethod]
    public async Task MetadataMatchChangedAfterPreparationFailsWithoutReplacingIt()
    {
        if (!RequireWindows()) return;
        using var temp = new TempScope();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var sourceFile = Path.Combine(source, "a.bin");
        var target = Path.Combine(targetRoot, "a.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3]);
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(sourceFile));
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceMetadataDifferent, false);
        var prepared = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [plan, CancellationToken.None])!;
        var roots = (string[])prepared.GetType().GetProperty("DestinationRoots")!.GetValue(prepared)!;
        var progress = roots.Select(path => new DestinationProgress(path, 3, 1)).ToArray();
        await using var job = new CopyJob(progress);
        job.SetPaused(true);
        var running = (Task)typeof(CopyEngine).GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [prepared, progress, new CopyOptions(Verify: false), job])!;
        job.Attach(running);
        await File.WriteAllBytesAsync(target, [9, 9, 9, 9]);
        job.SetPaused(false);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(DestinationPhase.Failed, job.Snapshot().Single().Phase);
        Assert.AreEqual(0UL, job.Snapshot().Single().FilesSkipped);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(target));
    }

    private static bool RequireWindows()
    {
        if (OperatingSystem.IsWindows()) return true;
        Assert.Inconclusive("Requires Windows storage topology and file sharing semantics.");
        return false;
    }

    private sealed class TempScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"metadata-comparison-{Guid.NewGuid():N}");
        public TempScope() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
