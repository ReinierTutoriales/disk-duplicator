using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class IndependentSourceReadTests
{
    [TestMethod]
    public async Task PoolsCannotBeHeldByAnotherDestination()
    {
        Assert.AreEqual(128 * 1024 * 1024, CopyEngine.IndependentPoolCapacity(2));
        Assert.AreEqual(64 * 1024 * 1024, CopyEngine.IndependentPoolCapacity(4));
        Assert.AreEqual(16 * 1024 * 1024, CopyEngine.IndependentPoolCapacity(16));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CopyEngine.IndependentPoolCapacity(17));
        using var slow = new SharedFanoutBufferPool(16 * 1024 * 1024);
        using var fast = new SharedFanoutBufferPool(16 * 1024 * 1024);
        using var held = await slow.RentAsync(16 * 1024 * 1024, 4096, 1, CancellationToken.None);
        using var independent = await fast.RentAsync(8 * 1024 * 1024, 4096, 1, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(16 * 1024 * 1024, slow.UsedBytes);
        Assert.AreEqual(8 * 1024 * 1024, fast.UsedBytes);
    }

    [TestMethod]
    public async Task TwoIndependentProducersCommitIdenticalBytesWithSeparateHashes()
    {
        // A local NVMe source: one reader and one pool per destination, each reading the whole source.
        var payload = Payload(9 * 1024 * 1024 + 37, 250);
        var (job, destinations, cleanup) = await RunWithSourceAsync(payload,
            device => device with { MediaKind = StorageMediaKind.SolidState, BusType = "NVMe", IsNetwork = false });
        if (job is null) return;
        await using (job)
        {
            try
            {
                foreach (var destination in destinations)
                    CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Source", "payload.bin")));
                Assert.IsTrue(job.Snapshot().All(snapshot => snapshot.Phase == DestinationPhase.Done));
                Assert.AreEqual(true, job.IndependentSourceReads);
                // Each destination records its own completion when its reader and writer finish.
                Assert.IsTrue(job.Snapshot().All(snapshot => snapshot.CopyFinishedAt is not null));
                Assert.AreEqual(2L * payload.Length, job.DiagnosticsSnapshot().SourceHashBytes);
                Assert.AreEqual(2L * payload.Length, job.DiagnosticsSnapshot().SourceReadBytes);
            }
            finally { cleanup(); }
        }
    }

    [TestMethod]
    public async Task SingleStreamSourceIsReadOnceWithReadAheadForEveryDestination()
    {
        // A USB hard disk: splitting it between readers divides its rate, so one shared reader feeds every
        // destination and keeps the next block in flight. Several blocks plus an unaligned tail.
        var payload = Payload(3 * 8 * 1024 * 1024 + 4096 + 37, 251);
        var (job, destinations, cleanup) = await RunWithSourceAsync(payload,
            device => device with { MediaKind = StorageMediaKind.Rotational, BusType = "USB", IsNetwork = false });
        if (job is null) return;
        await using (job)
        {
            try
            {
                foreach (var destination in destinations)
                    CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Source", "payload.bin")));
                Assert.IsTrue(job.Snapshot().All(snapshot => snapshot.Phase == DestinationPhase.Done));
                Assert.AreEqual(false, job.IndependentSourceReads);
                Assert.AreEqual((long)payload.Length, job.DiagnosticsSnapshot().SourceReadBytes);
                Assert.AreEqual((long)payload.Length, job.DiagnosticsSnapshot().SourceHashBytes);
                Assert.AreEqual(2L * payload.Length, job.DiagnosticsSnapshot().WrittenBytes);
            }
            finally { cleanup(); }
        }
    }

    private static byte[] Payload(int length, int seed)
    {
        var payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    // Real preflight, paths, writers, hashing and file-system I/O; only the source classification is
    // overridden, because the CI runner's disk does not report the media under test.
    private static async Task<(CopyJob? Job, string[] Destinations, Action Cleanup)> RunWithSourceAsync(
        byte[] payload, Func<StorageDeviceInfo, StorageDeviceInfo> classify)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires the Windows storage implementation.");
            return (null, [], () => { });
        }
        var root = Path.Combine(Path.GetTempPath(), $"RepartoCopier-independent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        void Cleanup() { try { Directory.Delete(root, recursive: true); } catch { } }
        var source = Directory.CreateDirectory(Path.Combine(root, "Source")).FullName;
        var destinations = new[] { "Fast", "Slow" }.Select(name =>
            Directory.CreateDirectory(Path.Combine(root, name)).FullName).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var plan = CopyPlan.Create(source, destinations, ExistingFilePolicy.ReplaceAll, keepGoing: false);
        var prepared = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [plan, CancellationToken.None])!;
        var sourceProperty = prepared.GetType().GetProperty("SourceDevice")!;
        sourceProperty.SetValue(prepared, classify((StorageDeviceInfo)sourceProperty.GetValue(prepared)!));
        var effective = (string[])prepared.GetType().GetProperty("DestinationRoots")!.GetValue(prepared)!;
        var progress = effective.Select(path => new DestinationProgress(path, (ulong)payload.Length, 1)).ToArray();
        var job = new CopyJob(progress);
        var run = (Task)typeof(CopyEngine).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [prepared, progress, new CopyOptions(), job])!;
        job.Attach(run);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));
        return (job, destinations, Cleanup);
    }

    [TestMethod]
    public void IndependentReadsAreUsedOnlyForSourcesThatServeParallelStreams()
    {
        var nvme = Device("C:\\", 0, "NVMe", StorageMediaKind.SolidState);
        var options = new CopyOptions();
        Assert.IsTrue(options.IndependentSourceReads, "Independent reads stay the default where they apply.");
        Assert.IsTrue(CopyEngine.UseIndependentSourceReads(options, nvme, 2));
        Assert.IsTrue(CopyEngine.UseIndependentSourceReads(options, nvme, 16));

        // Single-stream sources and out-of-range destination counts use the shared reader, never an error.
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options with { IndependentSourceReads = false }, nvme, 2));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, nvme, 1));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, nvme, 17));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, Device("D:\\", 1, "SATA", StorageMediaKind.SolidState), 3));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, Device("E:\\", 2, "SATA", StorageMediaKind.Rotational), 3));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, Device("F:\\", 3, "USB", StorageMediaKind.Rotational), 3));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, Device("G:\\", 4, "USB", StorageMediaKind.SolidState), 3));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, Device("H:\\", 5, "NVMe", StorageMediaKind.Unknown), 3));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, nvme with { IsNetwork = true }, 3));
    }

    private static StorageDeviceInfo Device(string root, uint number, string bus, StorageMediaKind media) =>
        new(root, root, number, 1, bus, media, false, 512, 4096, true, null);
}
