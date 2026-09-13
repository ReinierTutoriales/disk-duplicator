using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExplicitOffsetWriterTests
{
    [TestMethod]
    public async Task Qd2BlockFollowedByQd1TailPreservesExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-offset-{Guid.NewGuid():N}.bin");
        try
        {
            var first = new byte[8 * 1024 * 1024];
            var second = new byte[8 * 1024 * 1024];
            var tail = new byte[733];
            new Random(2026091301).NextBytes(first);
            new Random(2026091302).NextBytes(second);
            new Random(2026091303).NextBytes(tail);

            using (var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 1,
            }))
            {
                SafeFileHandle handle = stream.SafeFileHandle;
                await ExplicitOffsetWriter.WriteTwoAsync(
                    handle,
                    first,
                    0,
                    second,
                    first.LongLength,
                    CancellationToken.None);

                // RandomAccess writes must not advance FileStream.Position. This is
                // the exact condition that made a later cursor-based tail unsafe.
                Assert.AreEqual(0L, stream.Position);

                // This simulates the final QD1 fallback after an earlier QD2 block.
                // FileStream.Position is intentionally untouched; correctness must
                // come exclusively from the explicit offset.
                await ExplicitOffsetWriter.WriteOneAsync(
                    handle,
                    tail,
                    first.LongLength + second.LongLength,
                    CancellationToken.None);
                Assert.AreEqual(0L, stream.Position);
                stream.Flush(flushToDisk: true);
            }

            var actual = await File.ReadAllBytesAsync(path);
            var expected = new byte[first.Length + second.Length + tail.Length];
            Buffer.BlockCopy(first, 0, expected, 0, first.Length);
            Buffer.BlockCopy(second, 0, expected, first.Length, second.Length);
            Buffer.BlockCopy(tail, 0, expected, first.Length + second.Length, tail.Length);

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
