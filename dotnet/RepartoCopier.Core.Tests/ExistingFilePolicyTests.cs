using System.Text.Json;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExistingFilePolicyTests
{
    [TestMethod]
    public void ExistenceScanNeverOpensFileContent()
    {
        using var temp = new TempScope("scan");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var present = Path.Combine(root, "present.bin");
        File.WriteAllBytes(present, [1, 2, 3]);

        // An exclusive handle would make any attempt to read the content fail.
        using var exclusive = new FileStream(present, FileMode.Open, FileAccess.Read, FileShare.None);
        var existing = PreflightSafety.FindExistingFiles([root], ["present.bin", "missing.bin"]);

        Assert.IsTrue(existing[0][0]);
        Assert.IsFalse(existing[1][0]);
    }

    [TestMethod]
    public void UndecidedPlanReportsCountsAndAtMostThreeExamplesPerDestination()
    {
        using var temp = new TempScope("report");
        var first = Directory.CreateDirectory(Path.Combine(temp.Path, "A")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temp.Path, "B")).FullName;
        string[] names = ["a.bin", "b.bin", "c.bin", "d.bin", "e.bin"];
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(first, name), [1]);
        File.WriteAllBytes(Path.Combine(second, "a.bin"), [1]);

        var existing = PreflightSafety.FindExistingFiles([first, second], names);
        var conflict = Assert.ThrowsExactly<ExistingFilesConflictException>(() =>
            PreflightSafety.ThrowIfExistingFiles([first, second], names, existing));

        Assert.AreEqual(2, conflict.Destinations.Count);
        Assert.AreEqual(5, conflict.Destinations[0].ExistingFiles);
        Assert.AreEqual(5, conflict.Destinations[0].TotalFiles);
        Assert.AreEqual(3, conflict.Destinations[0].Examples.Count);
        Assert.AreEqual(1, conflict.Destinations[1].ExistingFiles);
    }

    [TestMethod]
    public void NoExistingFilesIsNotAConflict()
    {
        using var temp = new TempScope("none");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        string[] names = ["a.bin", "b.bin"];

        var existing = PreflightSafety.FindExistingFiles([root], names);
        PreflightSafety.ThrowIfExistingFiles([root], names, existing);

        Assert.IsFalse(existing[0][0]);
        Assert.IsFalse(existing[1][0]);
    }

    [TestMethod]
    public void LegacyProfileNeverCarriesAuthorizationToReplace()
    {
        var restored = CopyProfileSerializer.Deserialize(
            """{"version":1,"source":"C:\\Source","destinations":["D:\\Backup"],"skipExisting":true,"continueOnError":false,"shutdownWhenFinished":false,"verifyAfterCopy":false}""");

        Assert.IsFalse(CopyProfileSerializer.Serialize(restored).Contains("skipExisting", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(restored.VerifyAfterCopy);
    }

    [TestMethod]
    public async Task UndecidedPlanWithExistingFilesFailsBeforeAnyDestinationIsTouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology.");
            return;
        }
        using var temp = new TempScope("undecided");
        var (source, destinationBase, existingFile) = await CreateScenarioAsync(temp, existing: ("a.bin", [9, 9, 9]));
        var plan = CopyPlan.Create(source, [destinationBase], existingFiles: null, keepGoing: false);

        var conflict = Assert.ThrowsExactly<ExistingFilesConflictException>(() => CopyEngine.Start(plan));

        Assert.AreEqual(1, conflict.Destinations.Single().ExistingFiles);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(existingFile));
        Assert.IsFalse(Directory.Exists(StateLayout.StateDirectoryFor(Path.GetDirectoryName(existingFile)!)));
    }

    [TestMethod]
    public async Task KeepExistingLeavesDifferentFilesUntouchedAndCopiesTheMissingOnes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("keep");
        var (source, destinationBase, existingFile) = await CreateScenarioAsync(temp, existing: ("a.bin", [9, 9, 9]));
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.KeepExisting, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(1UL, snapshot.FilesSkipped);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(existingFile));
        CollectionAssert.AreEqual(
            new byte[] { 4, 5, 6 },
            await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(existingFile)!, "b.bin")));
    }

    [TestMethod]
    public async Task ReplaceAllReplacesEveryFileThatAlreadyExisted()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("replace-all");
        var (source, destinationBase, existingFile) = await CreateScenarioAsync(temp, existing: ("a.bin", [9, 9, 9]));
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceAll, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(0UL, snapshot.FilesSkipped);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(existingFile));
    }

    [TestMethod]
    public async Task ReplaceDifferentSkipsIdenticalContentEvenWhenTheDateDiffers()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("replace-different");
        var (source, destinationBase, identicalFile) = await CreateScenarioAsync(temp, existing: ("a.bin", [1, 2, 3]));
        var effectiveRoot = Path.GetDirectoryName(identicalFile)!;
        var oldDate = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(identicalFile, oldDate);
        var differentFile = Path.Combine(effectiveRoot, "b.bin");
        await File.WriteAllBytesAsync(differentFile, [7, 7, 7]);
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceDifferent, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(1UL, snapshot.FilesSkipped);
        Assert.AreEqual(oldDate, File.GetLastWriteTimeUtc(identicalFile));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(differentFile));
    }

    [TestMethod]
    public void OnlyAnExplicitPolicyEverAuthorizesTouchingAnExistingFile()
    {
        Assert.AreEqual(PreflightSafety.ExistingFileAction.Copy, PreflightSafety.Decide(null, exists: false));
        Assert.AreEqual(PreflightSafety.ExistingFileAction.Copy, PreflightSafety.Decide(ExistingFilePolicy.ReplaceAll, exists: false));
        Assert.AreEqual(PreflightSafety.ExistingFileAction.Keep, PreflightSafety.Decide(ExistingFilePolicy.KeepExisting, exists: true));
        Assert.AreEqual(PreflightSafety.ExistingFileAction.ReplaceAllowed, PreflightSafety.Decide(ExistingFilePolicy.ReplaceDifferent, exists: true));
        Assert.AreEqual(PreflightSafety.ExistingFileAction.ReplaceAllowed, PreflightSafety.Decide(ExistingFilePolicy.ReplaceAll, exists: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => PreflightSafety.Decide(null, exists: true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PreflightSafety.Decide((ExistingFilePolicy)99, exists: true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PreflightSafety.Decide((ExistingFilePolicy)(-1), exists: false));
    }

    [TestMethod]
    public void UnknownPolicyIsRejectedByThePlanAndByTheEngineBeforeAnythingElse()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CopyPlan.Create("C:/Origen", ["D:/Destino"], (ExistingFilePolicy)99, keepGoing: false));

        // A plan built without Create (record constructor or `with`) must be rejected too, and before the
        // engine looks at the file system: neither this source nor this destination exists.
        var forged = new CopyPlan("Z:/no-existe", ["Y:/no-existe"], (ExistingFilePolicy)99, false);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CopyEngine.Start(forged));
    }

    [TestMethod]
    public void ProfilesFromVersionOneAreMigratedAndNeverCarryAuthorization()
    {
        var restored = CopyProfileSerializer.Deserialize(
            """{"version":1,"source":"C:\\Source","destinations":["D:\\Backup"],"skipExisting":true,"continueOnError":true,"shutdownWhenFinished":false}""");

        Assert.AreEqual(CopyProfile.CurrentVersion, restored.Version);
        Assert.IsTrue(restored.ContinueOnError);
        using var saved = JsonDocument.Parse(CopyProfileSerializer.Serialize(restored));
        Assert.AreEqual(2, saved.RootElement.GetProperty("version").GetInt32());
        Assert.IsFalse(saved.RootElement.TryGetProperty("skipExisting", out _));
    }

    [TestMethod]
    public void SerializedProfilesAlwaysUseTheCurrentVersion()
    {
        var stale = new CopyProfile(1, @"C:\Source", [@"D:\Backup"], false, false, true);

        using var saved = JsonDocument.Parse(CopyProfileSerializer.Serialize(stale));

        Assert.AreEqual(CopyProfile.CurrentVersion, saved.RootElement.GetProperty("version").GetInt32());
    }

    [TestMethod]
    public void ProfileVersionsOutsideTheSupportedRangeAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => CopyProfileSerializer.Deserialize(
            """{"version":0,"source":"C:\\Source","destinations":["D:\\Backup"],"continueOnError":false,"shutdownWhenFinished":false}"""));
        Assert.ThrowsExactly<InvalidDataException>(() => CopyProfileSerializer.Deserialize(
            """{"version":3,"source":"C:\\Source","destinations":["D:\\Backup"],"continueOnError":false,"shutdownWhenFinished":false}"""));
    }

    [TestMethod]
    public async Task UndecidedPlanAfterRecoveryRestoredAFileStillAsksBeforeCopying()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology.");
            return;
        }
        using var temp = new TempScope("restored-undecided");
        var (source, destinationBase, restoredFile) = await CreateInterruptedReplacementAsync(temp);
        var plan = CopyPlan.Create(source, [destinationBase], existingFiles: null, keepGoing: false);

        // The file only exists after recovery restored its backup, so the first look finds no conflict.
        var conflict = Assert.ThrowsExactly<ExistingFilesConflictException>(() => CopyEngine.Start(plan));

        Assert.AreEqual(1, conflict.Destinations.Single().ExistingFiles);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(restoredFile));
    }

    [TestMethod]
    public async Task KeepExistingKeepsAFileThatRecoveryRestored()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("restored-keep");
        var (source, destinationBase, restoredFile) = await CreateInterruptedReplacementAsync(temp);
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.KeepExisting, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(0UL, snapshot.FilesErrored);
        Assert.AreEqual(1UL, snapshot.FilesSkipped);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(restoredFile));
    }

    [TestMethod]
    [DataRow(ExistingFilePolicy.ReplaceAll)]
    [DataRow(ExistingFilePolicy.ReplaceDifferent)]
    public async Task ReplacePoliciesReplaceAFileThatRecoveryRestoredWithoutFailingTheCommit(ExistingFilePolicy policy)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("restored-replace");
        var (source, destinationBase, restoredFile) = await CreateInterruptedReplacementAsync(temp);
        var plan = CopyPlan.Create(source, [destinationBase], policy, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(0UL, snapshot.FilesErrored);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(restoredFile));
    }

    [TestMethod]
    public async Task ReplaceDifferentPreparationDoesNotOpenExistingFileContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows file sharing semantics.");
            return;
        }
        using var temp = new TempScope("no-hash-in-preparation");
        var (source, destinationBase, existingFile) = await CreateScenarioAsync(temp, existing: ("a.bin", [1, 2, 3]));
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceDifferent, keepGoing: true);

        // Any attempt to read this file during preparation would fail under an exclusive handle. The
        // comparison belongs to the visible phase after the job starts.
        using var exclusive = new FileStream(existingFile, FileMode.Open, FileAccess.Read, FileShare.None);
        await using var job = await CopyEngine.StartAsync(plan, new CopyOptions(Verify: false, KeepGoing: true));
        job.RequestCancel();
        try { await job.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task AFileThatAppearsAfterAnalysisIsNeverReplaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage topology and Direct I/O.");
            return;
        }
        using var temp = new TempScope("appears-late");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), [4, 5, 6]);
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var plan = CopyPlan.Create(source, [destinationBase], ExistingFilePolicy.ReplaceAll, keepGoing: true);

        // Run the real engine with its pause gate closed before the producer can start.
        var prepared = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [plan, CancellationToken.None])!;
        var roots = (string[])prepared.GetType().GetProperty("DestinationRoots")!.GetValue(prepared)!;
        var progress = roots.Select(path => new DestinationProgress(path, 6, 2)).ToArray();
        await using var job = new CopyJob(progress);
        job.SetPaused(true);
        var running = (Task)typeof(CopyEngine).GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [prepared, progress, new CopyOptions(Verify: false, KeepGoing: true), job])!;
        job.Attach(running);
        var late = Path.Combine(destinationBase, "Source", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(late)!);
        await File.WriteAllBytesAsync(late, [9, 9, 9]);
        job.SetPaused(false);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(1UL, snapshot.FilesErrored);
        Assert.AreEqual("CompletedWithErrors", snapshot.Outcome);
        CollectionAssert.AreEqual(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(late));
        CollectionAssert.AreEqual(
            new byte[] { 4, 5, 6 },
            await File.ReadAllBytesAsync(Path.Combine(destinationBase, "Source", "b.bin")));
    }

    // a.bin = {1,2,3} in the source; the destination lost a.bin in an interrupted replacement: only the
    // backup {9,9,9} remains in the state folder, and recovery restores it when preparation starts.
    private static async Task<(string Source, string DestinationBase, string RestoredFile)> CreateInterruptedReplacementAsync(
        TempScope temp)
    {
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), [1, 2, 3]);
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var effectiveRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var restoredFile = Path.Combine(effectiveRoot, "a.bin");
        StateLayout.PrepareTempDirectory(effectiveRoot);
        await File.WriteAllBytesAsync(StateLayout.BackupPath(effectiveRoot, restoredFile), [9, 9, 9]);
        return (source, destinationBase, restoredFile);
    }

    // Source folder with a.bin = {1,2,3} and b.bin = {4,5,6}. The destination gets one pre-existing a.bin.
    private static async Task<(string Source, string DestinationBase, string ExistingFile)> CreateScenarioAsync(
        TempScope temp,
        (string Name, byte[] Content) existing)
    {
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "a.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.bin"), [4, 5, 6]);
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var effectiveRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var existingFile = Path.Combine(effectiveRoot, existing.Name);
        await File.WriteAllBytesAsync(existingFile, existing.Content);
        return (source, destinationBase, existingFile);
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RepartoCopier-existing-files-{name}-{Guid.NewGuid():N}");
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
