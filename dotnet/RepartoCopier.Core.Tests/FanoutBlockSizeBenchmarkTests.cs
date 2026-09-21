using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutBlockSizeBenchmarkTests
{
    private static readonly int[] CandidatesMiB = [1, 2, 4, 8, 16];

    [TestMethod]
    [TestCategory("Benchmark")]
    [Ignore("Run explicitly on target hardware; synthetic memory bandwidth must not choose production block size.")]
    public void MeasureSpillCopyCandidatesWithoutChangingProductionPolicy()
    {
        const int totalMiB = 512;
        foreach (var candidateMiB in CandidatesMiB)
        {
            var blockBytes = candidateMiB * 1024 * 1024;
            var source = new byte[blockBytes];
            Random.Shared.NextBytes(source);
            var iterations = totalMiB / candidateMiB;

            // Warm-up excludes first-use allocation/JIT noise.
            using (var warmup = FanoutSpillBlock.CopyFrom(source, 4096)) { }

            var started = Stopwatch.GetTimestamp();
            long copied = 0;
            for (var i = 0; i < iterations; i++)
            {
                using var spill = FanoutSpillBlock.CopyFrom(source, 4096);
                copied += spill.Length;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var mibPerSecond = copied / 1024d / 1024d / elapsed.TotalSeconds;
            Console.WriteLine($"FANOUT_BLOCK_BENCH size={candidateMiB}MiB copied={copied / 1024 / 1024}MiB elapsed={elapsed.TotalMilliseconds:F2}ms throughput={mibPerSecond:F2}MiB/s");
        }
    }

    [TestMethod]
    public void CandidateSetCoversOneThroughSixteenMiBAndKeepsEightMiBProductionDefault()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 8, 16 }, CandidatesMiB);
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("SharedFanoutBlockBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
