using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FastCrc32Tests
{
    [TestMethod]
    public void MatchesStandardCrc32CheckVector()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual(0xCBF43926u, FastCrc32.Compute(bytes));
    }

    [TestMethod]
    public void DifferentPayloadsProduceDifferentBlockChecksums()
    {
        var first = new byte[1024 * 1024];
        var second = new byte[first.Length];
        second[^1] = 1;
        Assert.AreNotEqual(FastCrc32.Compute(first), FastCrc32.Compute(second));
    }
}
