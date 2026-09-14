using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExplicitOffsetWriterTests
{
    [TestMethod]
    public async Task VariableDepthBlockFollowedByTailPreservesExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-offset-{Guid.NewGuid():N}.bin");
        try
        {
            var block = new byte[32 * 1024 * 1024];
            var tail = new byte[733];
            new Random(2026091301).NextBytes(block);
            new Random(2026091303).NextBytes(tail);

            using var scheduler = new DeviceScheduler("test-device", 16, 64L * 1024 * 1024);
            using (var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1,
            }))
            {
                var blockOperations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    block,
                    0,
                    requestedDepth: 16,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    scheduler,
                    CancellationToken.None);

                var tailOperations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    tail,
                    block.LongLength,
                    requestedDepth: 16,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    scheduler,
                    CancellationToken.None);

                Assert.AreEqual(16, blockOperations);
                Assert.AreEqual(1, tailOperations);
                Assert.AreEqual(0L, stream.Position, "RandomAccess no debe depender del cursor de FileStream.");
                stream.Flush(flushToDisk: true);
            }

            var actual = await File.ReadAllBytesAsync(path);
            var expected = new byte[block.Length + tail.Length];
            Buffer.BlockCopy(block, 0, expected, 0, block.Length);
            Buffer.BlockCopy(tail, 0, expected, block.Length, tail.Length);

            Assert.AreEqual(expected.LongLength, actual.LongLength);
            CollectionAssert.AreEqual(SHA256.HashData(expected), SHA256.HashData(actual));
            CollectionAssert.AreEqual(expected, actual);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
