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
    public void EmptyPayloadMatchesStandardCrc32Identity()
    {
        Assert.AreEqual(0u, FastCrc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void DifferentPayloadsProduceDifferentBlockChecksums()
    {
        var first = new byte[1024 * 1024];
        var second = new byte[first.Length];
        second[^1] = 1;
        Assert.AreNotEqual(FastCrc32.Compute(first), FastCrc32.Compute(second));
    }

    [TestMethod]
    public void SlicingBoundariesMatchByteWiseReference()
    {
        int[] lengths = [1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 4095, 4096, 4097];
        foreach (var length in lengths)
        {
            var data = new byte[length];
            new Random(length * 7919).NextBytes(data);
            Assert.AreEqual(
                ComputeReference(data),
                FastCrc32.Compute(data),
                $"CRC32 divergente en longitud de borde {length}.");
        }
    }

    [TestMethod]
    public void RandomPayloadsMatchByteWiseReference()
    {
        var random = new Random(0x5A17C32);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var length = random.Next(0, 256 * 1024);
            var data = new byte[length];
            random.NextBytes(data);
            Assert.AreEqual(ComputeReference(data), FastCrc32.Compute(data), $"CRC32 divergente en longitud {length}.");
        }
    }

    private static uint ComputeReference(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0xEDB88320u;
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
        }
        return ~crc;
    }
}
