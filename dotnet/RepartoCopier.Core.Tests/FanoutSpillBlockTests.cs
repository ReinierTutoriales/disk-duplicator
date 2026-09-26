using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSpillBlockTests
{
    [TestMethod]
    public void SpillBlockPreservesBytesAndDirectIoAlignment()
    {
        var source = new byte[8 * 1024 * 1024];
        new Random(7319).NextBytes(source);
        using var block = FanoutSpillBlock.CopyFrom(source, 64 * 1024);

        Assert.AreEqual(source.Length, block.Length);
        Assert.IsTrue(block.IsAlignedFor(4096));
        Assert.IsTrue(block.IsAlignedFor(64 * 1024));
        CollectionAssert.AreEqual(source, block.Memory.ToArray());
    }
}
