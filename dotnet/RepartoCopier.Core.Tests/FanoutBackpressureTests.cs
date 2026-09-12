using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutBackpressureTests
{
    [TestMethod]
    public async Task EmptyAndTinyFilesUseBoundedControlPlaneWithoutLosingOrder()
    {
        using var temp = new TempDirectory("control-plane");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        const int files = 256;
        for (var index = 0; index < files; index++)
        {
            var path = Path.Combine(source, $"f-{index:D4}.bin");
            if ((index & 1) == 0)
                File.WriteAllBytes(path, []);
            else
                File.WriteAllBytes(path, [(byte)(index & 0xff)]);
        }

        var destinations = Enumerable.Range(0, 3)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        AssertHealthy(job);
        Assert.IsTrue(job.DiagnosticsSnapshot().PeakControlBacklogMessages > 0);
        Assert.IsTrue(job.DiagnosticsSnapshot().CopyPhaseElapsed > TimeSpan.Zero);
        foreach (var destination in destinations)
        {
            var root = Path.Combine(destination, "Origen");
            Assert.AreEqual(files, Directory.EnumerateFiles(root).Count());
            for (var index = 0; index < files; index++)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, $"f-{index:D4}.bin"));
                if ((index & 1) == 0)
                    Assert.AreEqual(0, bytes.Length);
                else
                    CollectionAssert.AreEqual(new byte[] { (byte)(index & 0xff) }, bytes);
            }
        }
    }

    [TestMethod]
    public async Task VerificationTelemetrySeparatesGovernorWaitFromHashCompute()
    {
        using var temp = new TempDirectory("verify-telemetry");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[8 * 1024 * 1024 + 113];
        new Random(424242).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destinations = Enumerable.Range(0, 3)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        await using var job = CopyEngine.Start(
            CopyPlan.Create(source, destinations, false, false),
            new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        AssertHealthy(job);

        var metrics = job.DiagnosticsSnapshot();
        Assert.IsTrue(metrics.VerifyReadBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.VerifyHashBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.VerifyHashTime > TimeSpan.Zero);
        Assert.IsTrue(metrics.VerifyCpuWaitTime >= TimeSpan.Zero);
        Assert.IsTrue(metrics.VerifyPhaseElapsed > TimeSpan.Zero);
    }

    private static void AssertHealthy(CopyJob job)
    {
        var bad = job.Snapshot().Where(item => item.Phase != DestinationPhase.Done).ToArray();
        if (bad.Length == 0) return;
        Assert.Fail(string.Join(" | ", bad.Select(item => $"{item.Label}: {item.Phase}: {item.Error}")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"repartocopier-fanout-{name}-{Guid.NewGuid():N}");
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
