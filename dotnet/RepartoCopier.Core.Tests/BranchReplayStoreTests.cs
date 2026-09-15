using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchReplayStoreTests
{
    [TestMethod]
    public async Task ReplayStoreRoundTripsIndependentSegmentsAndCrcMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepartoCopier-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BranchReplayStore(directory);
            var first = new byte[257 * 1024 + 19];
            var second = new byte[513 * 1024 + 7];
            new Random(20260914).NextBytes(first);
            new Random(20260915).NextBytes(second);

            var firstCrc = FastCrc32C.Compute(first);
            var secondCrc = FastCrc32C.Compute(second);
            var firstSegment = await store.SpillAsync(first, firstCrc, CancellationToken.None);
            var secondSegment = await store.SpillAsync(second, secondCrc, CancellationToken.None);

            Assert.AreEqual(first.Length, firstSegment.Length);
            Assert.AreEqual(second.Length, secondSegment.Length);
            Assert.AreEqual(firstCrc, firstSegment.VerificationCrc32C);
            Assert.AreEqual(secondCrc, secondSegment.VerificationCrc32C);
            Assert.IsTrue(secondSegment.Offset >= firstSegment.Offset + firstSegment.Length);

            var firstRead = new byte[first.Length];
            var secondRead = new byte[second.Length];
            await store.ReadAsync(firstSegment, firstRead, CancellationToken.None);
            await store.ReadAsync(secondSegment, secondRead, CancellationToken.None);
            CollectionAssert.AreEqual(first, firstRead);
            CollectionAssert.AreEqual(second, secondRead);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task DisabledStoreRefusesDiskReplay()
    {
        using var store = new BranchReplayStore(null);
        Assert.IsFalse(store.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SpillAsync(new byte[4096], 0, CancellationToken.None));
    }
}