using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchReplayStoreTests
{
    [TestMethod]
    public async Task ReplayStoreRoundTripsIndependentSegmentsAndCrcMetadata()
    {
        using var store = new BranchReplayStore();
        var first = new byte[257 * 1024 + 19];
        var second = new byte[513 * 1024 + 7];
        new Random(20260914).NextBytes(first);
        new Random(20260915).NextBytes(second);

        var firstCrc = FastCrc32.Compute(first);
        var secondCrc = FastCrc32.Compute(second);
        var firstSegment = await store.SpillAsync(first, firstCrc, CancellationToken.None);
        var secondSegment = await store.SpillAsync(second, secondCrc, CancellationToken.None);

        Assert.AreEqual(first.Length, firstSegment.Length);
        Assert.AreEqual(second.Length, secondSegment.Length);
        Assert.AreEqual(firstCrc, firstSegment.VerificationCrc32);
        Assert.AreEqual(secondCrc, secondSegment.VerificationCrc32);
        Assert.IsTrue(secondSegment.Offset >= firstSegment.Offset + firstSegment.Length);

        var firstRead = new byte[first.Length];
        var secondRead = new byte[second.Length];
        await store.ReadAsync(firstSegment, firstRead, CancellationToken.None);
        await store.ReadAsync(secondSegment, secondRead, CancellationToken.None);
        CollectionAssert.AreEqual(first, firstRead);
        CollectionAssert.AreEqual(second, secondRead);
    }
}
