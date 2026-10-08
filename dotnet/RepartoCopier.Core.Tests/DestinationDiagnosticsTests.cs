using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DestinationDiagnosticsTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static StorageDeviceInfo UsbHdd(string root) => new(
        root, root, 5, 1, "USB", StorageMediaKind.Rotational, true, 512, 4096, true, null,
        SharesPhysicalDevice: true);

    [TestMethod]
    public void NormalCompletionRecordsOrderedMarksIdentityAndWriteTime()
    {
        var progress = new DestinationProgress("D:\\ISOS", 2048, 2);
        progress.SetDevice("PhysicalDisk5", UsbHdd("D:\\ISOS"));
        progress.SetPhase(DestinationPhase.Copying);
        progress.AddWriteTime(TimeSpan.FromMilliseconds(30));
        progress.AddWriteTime(TimeSpan.FromMilliseconds(20));
        progress.AddDurableFlush(TimeSpan.FromMilliseconds(10));
        progress.AddDurableFlush(TimeSpan.FromMilliseconds(15));
        progress.AddWritten(2048);
        progress.MarkDone();
        progress.MarkDone();
        progress.MarkCopyFinished();
        progress.SetVerifyWork(2048, 2);
        progress.SetPhase(DestinationPhase.Verifying);
        progress.MarkVerifyFileDone();
        progress.MarkVerifyFileDone();
        progress.SetPhase(DestinationPhase.Done);

        var snapshot = progress.Snapshot();
        Assert.AreEqual("PhysicalDisk5", snapshot.DeviceId);
        Assert.IsNotNull(snapshot.TrackingStartedAt);
        Assert.AreEqual(5u, snapshot.PhysicalDeviceNumber);
        Assert.AreEqual("USB", snapshot.BusType);
        Assert.AreEqual(StorageMediaKind.Rotational, snapshot.MediaKind);
        Assert.AreEqual(true, snapshot.Removable);
        Assert.IsTrue(snapshot.SharesPhysicalDevice);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), snapshot.WriteTime);
        Assert.AreEqual(2UL, snapshot.DurableFlushes);
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), snapshot.DurableFlushTime);
        Assert.AreEqual("Completed", snapshot.Outcome);

        CollectionAssert.AreEqual(
            new[] { DestinationPhase.Copying, DestinationPhase.Verifying, DestinationPhase.Done },
            snapshot.PhaseMarks.Select(mark => mark.Phase).ToArray());
        Assert.IsNotNull(snapshot.CopyStartedAt);
        Assert.IsNotNull(snapshot.CopyFinishedAt);
        Assert.IsNotNull(snapshot.VerifyStartedAt);
        Assert.IsNotNull(snapshot.VerifyFinishedAt);
        Assert.IsTrue(snapshot.CopyStartedAt <= snapshot.CopyFinishedAt);
        Assert.IsTrue(snapshot.CopyFinishedAt <= snapshot.VerifyStartedAt);
        Assert.IsTrue(snapshot.VerifyStartedAt <= snapshot.VerifyFinishedAt);
        Assert.AreEqual(snapshot.CopyFinishedAt - snapshot.CopyStartedAt, snapshot.CopyDuration);
        Assert.AreEqual(snapshot.VerifyFinishedAt - snapshot.VerifyStartedAt, snapshot.VerifyDuration);
    }

    [TestMethod]
    public void InterruptedCopyThatDidNotFinishEveryFileNeverReceivesAFinishMark()
    {
        var progress = new DestinationProgress("F:\\ISOS", 2048, 2);
        progress.SetPhase(DestinationPhase.Copying);
        progress.AddWritten(1024);
        progress.MarkDone();

        progress.MarkCopyFinished();

        Assert.IsNull(progress.Snapshot().CopyFinishedAt);
        Assert.IsNull(progress.Snapshot().CopyDuration);
        Assert.AreEqual("InProgress", progress.Snapshot().Outcome);
    }

    [TestMethod]
    public void CancelledDestinationKeepsItsStateAndNeverGetsACompletionMark()
    {
        var progress = new DestinationProgress("G:\\ISOS", 1024, 1);
        progress.SetPhase(DestinationPhase.Copying);
        progress.MarkDone();
        progress.SetPhase(DestinationPhase.Cancelled, "Cancelado");

        progress.MarkCopyFinished();

        var snapshot = progress.Snapshot();
        Assert.AreEqual(DestinationPhase.Cancelled, snapshot.Phase);
        Assert.AreEqual("Cancelled", snapshot.Outcome);
        Assert.IsNull(snapshot.CopyFinishedAt);
        Assert.IsNull(snapshot.VerifyFinishedAt);
        Assert.AreEqual(DestinationPhase.Cancelled, snapshot.PhaseMarks[^1].Phase);
    }

    [TestMethod]
    public void FailureDuringVerifyKeepsCopyMarksButNeverGetsAVerifyFinishMark()
    {
        var progress = new DestinationProgress("I:\\ISOS", 1024, 1);
        progress.SetPhase(DestinationPhase.Copying);
        progress.AddWritten(1024);
        progress.MarkDone();
        progress.MarkCopyFinished();
        progress.SetVerifyWork(1024, 1);
        progress.SetPhase(DestinationPhase.Verifying);
        progress.SetPhase(DestinationPhase.Failed, "Fallo de lectura");

        progress.MarkVerifyFileDone();

        var snapshot = progress.Snapshot();
        Assert.AreEqual("Failed", snapshot.Outcome);
        Assert.IsNotNull(snapshot.CopyFinishedAt);
        Assert.IsNull(snapshot.VerifyFinishedAt);
        Assert.IsNull(snapshot.VerifyDuration);
    }

    [TestMethod]
    public void FileErrorsCountTowardCopyCompletionAndAreVisibleInTheOutcome()
    {
        var progress = new DestinationProgress("D:\\ISOS", 2048, 2);
        progress.SetPhase(DestinationPhase.Copying);
        progress.MarkDone();
        progress.MarkError("a.iso: acceso denegado");

        progress.MarkCopyFinished();
        progress.SetPhase(DestinationPhase.Done);

        var snapshot = progress.Snapshot();
        Assert.IsNotNull(snapshot.CopyFinishedAt);
        Assert.AreEqual("CompletedWithErrors", snapshot.Outcome);
    }

    [TestMethod]
    public void RepeatedPhaseUpdatesDoNotDuplicateMarks()
    {
        var progress = new DestinationProgress("D:\\ISOS", 0, 0);
        progress.SetPhase(DestinationPhase.Copying);
        progress.SetPhase(DestinationPhase.Copying);

        Assert.AreEqual(1, progress.Snapshot().PhaseMarks.Count);
    }

    [TestMethod]
    public void ExportUsesSchemaThreeKeepsApplicationVersionAndEmbedsTheBuildRevision()
    {
        var progress = new DestinationProgress("D:\\ISOS", 1024, 1);
        progress.SetDevice("PhysicalDisk5", UsbHdd("D:\\ISOS"));
        progress.SetPhase(DestinationPhase.Copying);
        progress.AddWriteTime(TimeSpan.FromSeconds(1));
        progress.AddDurableFlush(TimeSpan.FromMilliseconds(10));
        progress.AddWritten(1024);
        progress.MarkDone();
        progress.MarkCopyFinished();
        progress.SetPhase(DestinationPhase.Done);

        var json = DiagnosticsExport.Serialize(DiagnosticsExport.Create(
            "2.1.1.0", $"2.1.1+{Sha}", DateTimeOffset.Parse("2026-10-06T15:49:19-04:00"), null,
            [progress.Snapshot()]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(5, root.GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual("2.1.1.0", root.GetProperty("ApplicationVersion").GetString());
        Assert.AreEqual(Sha, root.GetProperty("BuildRevision").GetString());
        Assert.AreEqual($"2.1.1+{Sha}", root.GetProperty("BuildInformationalVersion").GetString());

        var destination = root.GetProperty("Destinations")[0];
        Assert.AreEqual("Done", destination.GetProperty("Phase").GetString());
        Assert.AreEqual("Completed", destination.GetProperty("Outcome").GetString());
        Assert.AreEqual("PhysicalDisk5", destination.GetProperty("DeviceId").GetString());
        Assert.AreEqual(TimeSpan.Zero, destination.GetProperty("TrackingStartedAt").GetDateTimeOffset().Offset);
        Assert.AreEqual("Rotational", destination.GetProperty("MediaKind").GetString());
        Assert.AreEqual(1UL, destination.GetProperty("DurableFlushes").GetUInt64());
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), TimeSpan.Parse(
            destination.GetProperty("DurableFlushTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            TimeSpan.Parse(destination.GetProperty("WriteTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("Copying", destination.GetProperty("PhaseMarks")[0].GetProperty("Phase").GetString());
        Assert.AreNotEqual(JsonValueKind.Null, destination.GetProperty("CopyFinishedAt").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, destination.GetProperty("VerifyFinishedAt").ValueKind);
    }

    [TestMethod]
    public void ExportNotesDocumentWhichTimesAreAccumulatedAndWhichIncludeWaits()
    {
        var notes = DiagnosticsExport.MeasurementNotes;

        Assert.IsTrue(notes.Any(note => note.Contains("summed across concurrent destinations", StringComparison.Ordinal)));
        Assert.IsTrue(notes.Any(note => note.Contains("BufferWaitTime", StringComparison.Ordinal) &&
                                         note.Contains("immediate rentals", StringComparison.Ordinal)));
        Assert.IsTrue(notes.Any(note => note.Contains("per-device I/O gate", StringComparison.Ordinal)));
        Assert.IsTrue(notes.Any(note => note.Contains("offsets from the creation", StringComparison.Ordinal)));
        Assert.IsTrue(notes.Any(note => note.Contains("never receive a completion mark", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BuildRevisionIsReadFromTheInformationalVersion()
    {
        Assert.AreEqual(Sha, BuildInfo.Revision($"2.1.1+{Sha}"));
        Assert.IsNull(BuildInfo.Revision("2.1.1"));
        Assert.IsNull(BuildInfo.Revision("2.1.1+"));
        Assert.IsNull(BuildInfo.Revision(null));
        Assert.IsNull(BuildInfo.Revision("  "));
        Assert.IsNull(BuildInfo.Revision("2.1.1+not-a-sha"));
        Assert.IsNull(BuildInfo.Revision("2.1.1+" + new string('z', 40)));
    }

    [TestMethod]
    public async Task EngineRecordsDeviceIdentityWriteTimeAndOrderedMarksOnNormalCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires the Windows storage topology and Direct I/O paths.");
            return;
        }
        using var temp = new TempScope("destination-diagnostics");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var payload = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var first = Directory.CreateDirectory(Path.Combine(temp.Path, "A")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(temp.Path, "B")).FullName;

        var plan = CopyPlan.Create(source, [first, second], existingFiles: ExistingFilePolicy.ReplaceAll, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        foreach (var snapshot in job.Snapshot())
        {
            Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
            Assert.AreEqual("Completed", snapshot.Outcome);
            Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.DeviceId));
            Assert.IsTrue(snapshot.WriteTime > TimeSpan.Zero);
            Assert.AreEqual(1UL, snapshot.DurableFlushes);
            Assert.IsTrue(snapshot.DurableFlushTime > TimeSpan.Zero);
            Assert.IsNotNull(snapshot.CopyStartedAt);
            Assert.IsNotNull(snapshot.CopyFinishedAt);
            Assert.IsNotNull(snapshot.VerifyStartedAt);
            Assert.IsNotNull(snapshot.VerifyFinishedAt);
            Assert.IsTrue(snapshot.CopyStartedAt <= snapshot.CopyFinishedAt);
            Assert.IsTrue(snapshot.CopyFinishedAt <= snapshot.VerifyStartedAt);
            Assert.IsTrue(snapshot.VerifyStartedAt <= snapshot.VerifyFinishedAt);
            Assert.AreEqual(DestinationPhase.Copying, snapshot.PhaseMarks[0].Phase);
            Assert.AreEqual(DestinationPhase.Done, snapshot.PhaseMarks[^1].Phase);
        }
    }

    [TestMethod]
    public async Task EngineCancellationKeepsCancelledStateWithoutCompletionMarks()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires the Windows storage topology and Direct I/O paths.");
            return;
        }
        using var temp = new TempScope("destination-diagnostics-cancel");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var payload = new byte[48 * 1024 * 1024];
        new Random(7).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;

        var plan = CopyPlan.Create(source, [destination], existingFiles: ExistingFilePolicy.ReplaceAll, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, KeepGoing: false));
        job.SetPaused(true);
        job.RequestCancel();
        try { await job.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (OperationCanceledException) { }

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Cancelled, snapshot.Phase);
        Assert.AreEqual("Cancelled", snapshot.Outcome);
        Assert.IsNull(snapshot.CopyFinishedAt);
        Assert.IsNull(snapshot.VerifyFinishedAt);
        Assert.AreEqual(DestinationPhase.Cancelled, snapshot.PhaseMarks[^1].Phase);
    }

    private sealed class TempScope : IDisposable
    {
        public TempScope(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RepartoCopier-{name}-{Guid.NewGuid():N}");
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
