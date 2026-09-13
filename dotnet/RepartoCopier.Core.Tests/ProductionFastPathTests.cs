using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ProductionFastPathTests
{
    // Production intentionally ends after the durable copy path; full physical read-back
    // remains an explicit diagnostic/test mode rather than mandatory user-facing work.
    [TestMethod]
    public async Task DefaultProductionCopySkipsPhysicalReadBackAndPreservesBytes()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[20 * 1024 * 1024 + 733];
        new Random(20260912).NextBytes(payload);
        var sourceFile = Path.Combine(source, "payload.bin");
        await File.WriteAllBytesAsync(sourceFile, payload);

        var destinations = Enumerable.Range(0, 2)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);

        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
        var metrics = job.DiagnosticsSnapshot();
        Assert.AreEqual(0L, metrics.VerifyReadBytes);
        Assert.AreEqual(0L, metrics.VerifyHashBytes);
        Assert.AreEqual(TimeSpan.Zero, metrics.VerifyPhaseElapsed);

        var expected = SHA256.HashData(payload);
        foreach (var destination in destinations)
        {
            var copied = Path.Combine(destination, "Origen", "payload.bin");
            Assert.IsTrue(File.Exists(copied));
            Assert.AreEqual(payload.LongLength, new FileInfo(copied).Length);
            await using var stream = File.OpenRead(copied);
            CollectionAssert.AreEqual(expected, await SHA256.HashDataAsync(stream));
        }
    }

    [TestMethod]
    public async Task LargeSharedBlocksAreWrittenWithOneIoOperationPerDestinationBlock()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[20 * 1024 * 1024 + 733];
        new Random(20260913).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);

        var destinations = Enumerable.Range(0, 2)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-write-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);

        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
        var metrics = job.DiagnosticsSnapshot();
        // 20 MiB + 733 bytes uses two source blocks (16 MiB + tail). With two
        // destinations the optimized writer must issue exactly four WriteAsync calls.
        Assert.AreEqual(4L, metrics.WriteOperations);
    }

    [TestMethod]
    public async Task ExplicitDiagnosticVerificationRemainsAvailable()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "verify.bin"), new byte[6 * 1024 * 1024 + 17]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var plan = CopyPlan.Create(source, [destination], skipSame: false, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyReadBytes > 0);
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyHashBytes > 0);
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyPhaseElapsed > TimeSpan.Zero);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repartocopier-fast-{Guid.NewGuid():N}");
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
