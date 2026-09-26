using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PhysicalDiskPerformanceBenchmarkTests
{
    [TestMethod]
    [TestCategory("PhysicalBenchmark")]
    public async Task MeasureRealSourceAndDestinationThroughput()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("REPARTOCOPIER_RUN_PHYSICAL_BENCHMARK"), "1", StringComparison.Ordinal))
            Assert.Inconclusive("Set REPARTOCOPIER_RUN_PHYSICAL_BENCHMARK=1 and the benchmark paths to run on target hardware.");

        var sourceBase = Environment.GetEnvironmentVariable("REPARTOCOPIER_BENCH_SOURCE");
        var destinationValue = Environment.GetEnvironmentVariable("REPARTOCOPIER_BENCH_DESTINATIONS");
        Assert.IsFalse(string.IsNullOrWhiteSpace(sourceBase));
        Assert.IsFalse(string.IsNullOrWhiteSpace(destinationValue));

        var destinations = destinationValue!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        Assert.IsGreaterThan(0, destinations.Length);

        var sizeGiB = int.TryParse(Environment.GetEnvironmentVariable("REPARTOCOPIER_BENCH_GIB"), out var parsed)
            ? Math.Clamp(parsed, 1, 256)
            : 4;
        var verify = !string.Equals(
            Environment.GetEnvironmentVariable("REPARTOCOPIER_BENCH_VERIFY"),
            "0",
            StringComparison.OrdinalIgnoreCase);

        var runId = $"repartocopier-bench-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var sourceRoot = Path.Combine(Path.GetFullPath(sourceBase!), runId);
        var destinationRoots = destinations.Select(root => Path.Combine(root, runId)).ToArray();

        Directory.CreateDirectory(sourceRoot);
        foreach (var root in destinationRoots)
            Directory.CreateDirectory(root);

        var sourceFile = Path.Combine(sourceRoot, "payload.bin");
        var bytes = checked((long)sizeGiB * 1024 * 1024 * 1024);
        await CreateDeterministicFileAsync(sourceFile, bytes);

        // Use directory-copy semantics: each supplied destination remains a root,
        // and the benchmark payload lands at <destination>/<runId>/payload.bin.
        var plan = CopyPlan.Create(sourceRoot, destinations, skipSame: false, keepGoing: false);
        var wall = Stopwatch.StartNew();
        await using var job = CopyEngine.Start(
            plan,
            new CopyOptions(Verify: verify, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromHours(2));
        wall.Stop();

        var snapshots = job.Snapshot();
        Assert.IsTrue(snapshots.All(item => item.Phase == DestinationPhase.Done));
        foreach (var root in destinationRoots)
        {
            var output = Path.Combine(root, "payload.bin");
            Assert.IsTrue(File.Exists(output));
            Assert.AreEqual(bytes, new FileInfo(output).Length);
        }

        var diagnostics = job.DiagnosticsSnapshot();
        var report = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Source = sourceFile,
            Destinations = destinationRoots,
            Bytes = bytes,
            Verify = verify,
            WallSeconds = wall.Elapsed.TotalSeconds,
            LogicalSourceMiBPerSecond = bytes / 1024d / 1024d / wall.Elapsed.TotalSeconds,
            Diagnostics = diagnostics,
            DestinationSnapshots = snapshots,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("REPARTOCOPIER_PHYSICAL_BENCHMARK");
        Console.WriteLine(json);

        if (!string.Equals(Environment.GetEnvironmentVariable("REPARTOCOPIER_BENCH_KEEP"), "1", StringComparison.Ordinal))
        {
            try { Directory.Delete(sourceRoot, recursive: true); } catch { }
            foreach (var root in destinationRoots)
                try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task CreateDeterministicFileAsync(string path, long bytes)
    {
        const int chunkBytes = 8 * 1024 * 1024;
        var buffer = new byte[chunkBytes];
        new Random(20260921).NextBytes(buffer);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            chunkBytes,
            FileOptions.SequentialScan);
        long written = 0;
        while (written < bytes)
        {
            var count = checked((int)Math.Min(buffer.Length, bytes - written));
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }
}
