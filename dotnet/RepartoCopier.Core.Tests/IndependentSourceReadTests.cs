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
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires the Windows storage implementation.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), $"RepartoCopier-independent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "Source")).FullName;
            var destinations = new[] { "Fast", "Slow" }.Select(name =>
                Directory.CreateDirectory(Path.Combine(root, name)).FullName).ToArray();
            var payload = new byte[9 * 1024 * 1024 + 37];
            new Random(250).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
            var plan = CopyPlan.Create(source, destinations, ExistingFilePolicy.ReplaceAll, keepGoing: false);
            var prepared = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [plan, CancellationToken.None])!;
            var effective = (string[])prepared.GetType().GetProperty("DestinationRoots")!.GetValue(prepared)!;
            var progress = effective.Select(path => new DestinationProgress(path, (ulong)payload.Length, 1)).ToArray();
            await using var job = new CopyJob(progress);
            var run = (Task)typeof(CopyEngine).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [prepared, progress, new CopyOptions(IndependentSourceReads: true), job])!;
            job.Attach(run);
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(120));
            foreach (var destination in destinations)
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(
                    Path.Combine(destination, "Source", "payload.bin")));
            Assert.IsTrue(job.Snapshot().All(snapshot => snapshot.Phase == DestinationPhase.Done));
            Assert.IsTrue(job.IndependentSourceReads);
            Assert.AreEqual(2L * payload.Length, job.DiagnosticsSnapshot().SourceHashBytes);
            Assert.AreEqual(2L * payload.Length, job.DiagnosticsSnapshot().SourceReadBytes);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void IndependentReadsAreTheDefaultForAnySourceWithSeveralDestinations()
    {
        var options = new CopyOptions();
        Assert.IsTrue(options.IndependentSourceReads, "Independent reads are the default.");
        Assert.IsTrue(CopyEngine.UseIndependentSourceReads(options, 2));
        Assert.IsTrue(CopyEngine.UseIndependentSourceReads(options, 16));

        // Fallback to the single shared reader, never an error.
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options with { IndependentSourceReads = false }, 2));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, 1));
        Assert.IsFalse(CopyEngine.UseIndependentSourceReads(options, 17));
    }

    [TestMethod]
    public void OnlyLocalNvmeAndSataSsdSourcesReadInParallel()
    {
        Assert.IsFalse(CopyEngine.SerializeIndependentReads(Device("C:\\", 0, "NVMe", StorageMediaKind.SolidState)));
        Assert.IsFalse(CopyEngine.SerializeIndependentReads(Device("D:\\", 1, "SATA", StorageMediaKind.SolidState)));
        Assert.IsTrue(CopyEngine.SerializeIndependentReads(Device("E:\\", 2, "SATA", StorageMediaKind.Rotational)));
        Assert.IsTrue(CopyEngine.SerializeIndependentReads(Device("F:\\", 3, "USB", StorageMediaKind.SolidState)));
        Assert.IsTrue(CopyEngine.SerializeIndependentReads(Device("G:\\", 4, "USB", StorageMediaKind.Rotational)));
        Assert.IsTrue(CopyEngine.SerializeIndependentReads(Device("H:\\", 5, "NVMe", StorageMediaKind.Unknown)));
        Assert.IsTrue(CopyEngine.SerializeIndependentReads(
            Device("I:\\", 6, "NVMe", StorageMediaKind.SolidState) with { IsNetwork = true }));
    }

    private static StorageDeviceInfo Device(string root, uint number, string bus, StorageMediaKind media) =>
        new(root, root, number, 1, bus, media, false, 512, 4096, true, null);
}
