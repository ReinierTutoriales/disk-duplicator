using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ProgressTelemetryTests
{
    [TestMethod]
    public void WriteThroughputDecaysWithoutEventsAndTerminalPhaseIsZero()
    {
        var progress = new DestinationProgress("D:\\", 1024 * 1024, 1);
        progress.SetPhase(DestinationPhase.Copying);
        Thread.Sleep(80);
        progress.AddWritten(1024 * 1024);

        var initial = progress.Snapshot().RecentBytesPerSecond;
        Assert.IsTrue(initial > 0);

        var future = Stopwatch.GetTimestamp() + (long)(3.0 * Stopwatch.Frequency);
        var decayed = progress.Snapshot(future).RecentBytesPerSecond;
        Assert.IsTrue(decayed > 0);
        Assert.IsTrue(decayed < initial);

        progress.SetPhase(DestinationPhase.Done);
        Assert.AreEqual(0d, progress.Snapshot(future).RecentBytesPerSecond);
    }

    [TestMethod]
    public async Task VerifyProgressReachesPerDestinationTotals()
    {
        using var temp = new TempScope("verify-progress");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var payload = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;

        var plan = CopyPlan.Create(source, [destination], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual((ulong)payload.Length, snapshot.VerifyBytesTotal);
        Assert.AreEqual(snapshot.VerifyBytesTotal, snapshot.VerifiedBytes);
        Assert.AreEqual(1UL, snapshot.VerifyFilesTotal);
        Assert.AreEqual(snapshot.VerifyFilesTotal, snapshot.VerifyFilesDone);
        Assert.AreEqual(1.0, snapshot.VerifyFraction, 0.000001);
    }

    [TestMethod]
    public async Task AllSkipSameFilesDoNotCreateSecondVerifyWork()
    {
        using var temp = new TempScope("verify-skip");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var sourceFile = Path.Combine(source, "same.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4, 5]);
        var sourceTime = File.GetLastWriteTimeUtc(sourceFile);

        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;
        var effectiveRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Source")).FullName;
        var destinationFile = Path.Combine(effectiveRoot, "same.bin");
        File.Copy(sourceFile, destinationFile);
        File.SetLastWriteTimeUtc(destinationFile, sourceTime);

        var plan = CopyPlan.Create(source, [destinationBase], skipSame: true, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: true, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
        Assert.AreEqual(1UL, snapshot.FilesSkipped);
        Assert.AreEqual(0UL, snapshot.VerifyBytesTotal);
        Assert.AreEqual(0UL, snapshot.VerifiedBytes);
        Assert.AreEqual(0L, job.DiagnosticsSnapshot().VerifyReadBytes);
    }

    [TestMethod]
    public async Task DiagnosticsSnapshotIncludesProductionDeviceScheduler()
    {
        using var temp = new TempScope("device-scheduler-telemetry");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), new byte[2 * 1024 * 1024]);
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;

        var plan = CopyPlan.Create(source, [destinationBase], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var final = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, final.Phase, final.Error);

        var effectiveRoot = Path.Combine(destinationBase, "Source");
        var expectedDeviceId = StorageTopology
            .InspectDestinations([effectiveRoot])
            .Destinations
            .Single()
            .PhysicalDeviceId;

        var schedulers = job.DiagnosticsSnapshot().DeviceSchedulers;
        Assert.AreEqual(1, schedulers.Count);
        Assert.AreEqual(expectedDeviceId, schedulers[0].DeviceId);
        Assert.IsTrue(schedulers[0].MaxOutstandingIo >= 1);
        Assert.IsTrue(schedulers[0].PeakOutstandingIo >= 1);
    }

    [TestMethod]
    public async Task DiagnosticsSnapshotIncludesProductionPipelineGovernor()
    {
        using var temp = new TempScope("pipeline-governor-telemetry");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Source")).FullName;
        var sourceFile = Path.Combine(source, "payload.bin");
        await using (var stream = new FileStream(sourceFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(20L * 1024 * 1024);
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "Destination")).FullName;

        var plan = CopyPlan.Create(source, [destinationBase], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var final = job.Snapshot().Single();
        Assert.AreEqual(DestinationPhase.Done, final.Phase, final.Error);

        var governor = job.DiagnosticsSnapshot().PipelineGovernor;
        Assert.IsNotNull(governor);
        Assert.IsTrue(governor.SourceReadTime > TimeSpan.Zero);
        Assert.IsTrue(governor.CurrentPrefetchLimit >= 1);
        Assert.IsTrue(governor.CurrentPrefetchLimit <= 4);
        Assert.IsTrue(governor.MinimumObservedPrefetchLimit <= governor.CurrentPrefetchLimit);
        Assert.IsTrue(governor.MaximumObservedPrefetchLimit >= governor.CurrentPrefetchLimit);
        Assert.AreEqual(0, governor.InFlight);
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
