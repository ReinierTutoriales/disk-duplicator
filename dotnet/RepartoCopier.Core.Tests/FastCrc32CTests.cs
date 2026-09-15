using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FastCrc32CTests
{
    [TestMethod]
    public void MatchesStandardCrc32CCheckVector()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual(0xE3069283u, FastCrc32C.Compute(bytes));
    }

    [TestMethod]
    public void EmptyPayloadMatchesCrc32CIdentity()
    {
        Assert.AreEqual(0u, FastCrc32C.Compute(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void DifferentPayloadsProduceDifferentBlockChecksums()
    {
        var first = new byte[1024 * 1024];
        var second = new byte[first.Length];
        second[^1] = 1;
        Assert.AreNotEqual(FastCrc32C.Compute(first), FastCrc32C.Compute(second));
    }

    [TestMethod]
    public void SlicingBoundariesMatchByteWiseCrc32CReference()
    {
        int[] lengths = [1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 4095, 4096, 4097];
        foreach (var length in lengths)
        {
            var data = new byte[length];
            new Random(length * 7919).NextBytes(data);
            Assert.AreEqual(
                ComputeReference(data),
                FastCrc32C.ComputeSoftware(data),
                $"CRC32C software divergente en longitud de borde {length}.");
            Assert.AreEqual(
                FastCrc32C.ComputeSoftware(data),
                FastCrc32C.Compute(data),
                $"Fast path CRC32C divergente en longitud de borde {length}.");
        }
    }

    [TestMethod]
    public void RandomPayloadsMatchByteWiseCrc32CReference()
    {
        var random = new Random(0x5A17C32);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var length = random.Next(0, 256 * 1024);
            var data = new byte[length];
            random.NextBytes(data);
            Assert.AreEqual(
                ComputeReference(data),
                FastCrc32C.ComputeSoftware(data),
                $"CRC32C software divergente en longitud {length}.");
            Assert.AreEqual(
                FastCrc32C.ComputeSoftware(data),
                FastCrc32C.Compute(data),
                $"Fast path CRC32C divergente en longitud {length}.");
        }
    }

    [TestMethod]
    public void HardwareAndSoftwareImplementationsAreBitIdenticalWhenHardwareExists()
    {
        if (!FastCrc32C.IsHardwareAccelerated)
            return;

        var random = new Random(0x32C0FFEE);
        int[] lengths = [0, 1, 3, 4, 7, 8, 9, 31, 32, 33, 4096, 65537, 1024 * 1024];
        foreach (var length in lengths)
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.AreEqual(
                FastCrc32C.ComputeSoftware(data),
                FastCrc32C.ComputeHardware(data),
                $"Hardware/software CRC32C divergente en longitud {length}.");
        }
    }

    private static uint ComputeReference(ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0x82F63B78u;
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
