using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SharedFanoutBufferPoolTests
{
    [TestMethod]
    public async Task PagesRecycleOnlyAfterEveryDestinationReleases()
    {
        using var pool = new SharedFanoutBufferPool(4 * Environment.SystemPageSize);
        var bytes = 2 * Environment.SystemPageSize;
        var lease = await pool.RentAsync(bytes, Environment.SystemPageSize, 3, CancellationToken.None);
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.AreEqual(3, lease.RemainingReferences);

        Assert.IsFalse(lease.ReleaseReference());
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.IsFalse(lease.ReleaseReference());
        Assert.AreEqual(bytes, pool.UsedBytes);
        Assert.IsTrue(lease.ReleaseReference());
        Assert.AreEqual(0, pool.UsedBytes);
    }

    [TestMethod]
    public async Task FullPoolBackpressuresUntilReferencedPagesReturn()
    {
        var page = Environment.SystemPageSize;
        using var pool = new SharedFanoutBufferPool(2 * page);
        var first = await pool.RentAsync(2 * page, page, 1, CancellationToken.None);
        var blocked = pool.RentAsync(page, page, 1, CancellationToken.None).AsTask();
        Assert.IsFalse(blocked.IsCompleted);

        Assert.IsTrue(first.ReleaseReference());
        var second = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(page, pool.UsedBytes);
        Assert.IsTrue(second.ReleaseReference());
        Assert.AreEqual(0, pool.UsedBytes);
    }

    [TestMethod]
    public async Task LeasesArePinnedAndRespectDirectIoAlignment()
    {
        var page = Environment.SystemPageSize;
        using var pool = new SharedFanoutBufferPool(8 * page);
        var alignment = Math.Min(64 * 1024, Math.Max(page, 4096));
        var lease = await pool.RentAsync(page, alignment, 1, CancellationToken.None);
        Assert.IsTrue(lease.Buffer.IsPinned);
        Assert.IsTrue(lease.IsAlignedFor(alignment));
        Assert.IsTrue(lease.ReleaseReference());
    }
}
