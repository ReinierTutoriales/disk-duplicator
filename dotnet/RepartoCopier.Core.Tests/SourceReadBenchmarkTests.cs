using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SourceReadBenchmarkTests
{
    [TestMethod]
    public void CommandLineRejectsAmbiguousOrUnsafeArguments()
    {
        var command = SourceBenchmarkCommandLine.Parse(
            ["--source-bench", "C:\\source.iso", "C:\\result.json", "--readers", "1,2,4", "--seconds", "20"]);
        CollectionAssert.AreEqual(new[] { 1, 2, 4 }, command.Readers);
        Assert.AreEqual(20, command.Seconds);
        Assert.ThrowsExactly<ArgumentException>(() => SourceBenchmarkCommandLine.Parse(
            ["--source-bench", "C:\\source.iso", "C:\\source.iso"]));
        Assert.ThrowsExactly<ArgumentException>(() => SourceBenchmarkCommandLine.Parse(
            ["--source-bench", "C:\\source.iso", "C:\\result.json", "--readers", "1,1"]));
        Assert.ThrowsExactly<ArgumentException>(() => SourceBenchmarkCommandLine.Parse(
            ["--source-bench", "C:\\source.iso", "C:\\result.json", "--seconds", "0"]));
    }

    [TestMethod]
    public async Task ReadersReturnSameHashWithoutModifyingSource()
    {
        var file = Path.Combine(Path.GetTempPath(), $"source-bench-{Guid.NewGuid():N}.bin");
        try
        {
            var payload = new byte[8 * 1024 * 1024 + 37];
            new Random(12345).NextBytes(payload);
            await File.WriteAllBytesAsync(file, payload);
            var result = await SourceReadBenchmark.RunAsync(file, 2, 1);
            Assert.AreEqual(2, result.Measurements.Count);
            Assert.IsTrue(result.Measurements.All(item => item.CompletedPasses >= 1 && item.Bytes >= payload.Length));
            Assert.AreEqual(result.Measurements[0].LastHash, result.Measurements[1].LastHash);
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
