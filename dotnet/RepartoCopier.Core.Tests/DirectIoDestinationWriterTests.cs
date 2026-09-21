using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DirectIoDestinationWriterTests
{
    [TestMethod]
    public void LocalHddAndSsdFollowExtremeStyleNoBufferingEligibility()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational);
        var ssd = Device("NVMe", StorageMediaKind.SolidState);

        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(hdd, 64L * 1024 * 1024));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 64L * 1024 * 1024));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 4096));
        Assert.IsFalse(DirectIoDestinationWriter.IsEligible(ssd, 1));
        Assert.IsFalse(DirectIoDestinationWriter.IsEligible(ssd, 0));
    }

    [TestMethod]
    public void NetworkIsRejectedButAlignedUncertainLocalVolumeCanAttemptDirectWrite()
    {
        var network = Device("Network", StorageMediaKind.SolidState) with
        {
            IsNetwork = true,
            PhysicalDeviceNumber = null,
        };
        var uncertain = Device("USB", StorageMediaKind.SolidState) with
        {
            PhysicalDeviceNumber = null,
        };

        Assert.IsFalse(DirectIoDestinationWriter.IsEligible(network, 64L * 1024 * 1024));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(uncertain, 64L * 1024 * 1024));
    }

    [TestMethod]
    public async Task FinalUnalignedTailIsPaddedInternallyThenTruncatedToExactEof()
    {
        const int alignment = 4096;
        const int alignedBytes = 1024 * 1024;
        const int tailBytes = 193;
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-direct-tail-{Guid.NewGuid():N}.bin");
        try
        {
            using var payload = SourceBufferLease.RentAligned(alignedBytes + tailBytes, 64 * 1024);
            new Random(2026091401).NextBytes(payload.Memory.Span);
            var expected = payload.Memory.ToArray();

            using var handle = File.OpenHandle(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                FileOptions.SequentialScan);
            using var session = new DirectIoDestinationWriter.Session(handle, alignment);
            using var scheduler = new DeviceScheduler("PhysicalDiskTail", 8, 64L * 1024 * 1024);

            var operations = await session.WriteAsync(
                payload.Memory,
                0,
                alignedBytes + tailBytes,
                payload.IsAlignedFor(alignment),
                scheduler,
                CancellationToken.None);
            session.FinalizeLength(alignedBytes + tailBytes);
            session.FlushToDisk();

            Assert.AreEqual(2, operations, "El cuerpo alineado y el tail acolchado deben ser dos escrituras completas, sin fragmentación intrabloque.");
            Assert.AreEqual(alignedBytes + tailBytes, RandomAccess.GetLength(handle));

            session.Dispose();
            var actual = await File.ReadAllBytesAsync(path);
            Assert.AreEqual(expected.LongLength, actual.LongLength);
            CollectionAssert.AreEqual(expected, actual);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [TestMethod]
    public async Task ConcurrentQueueDepthWritesPreserveExplicitOffsets()
    {
        const int blockBytes = 1024 * 1024;
        const int blockCount = 8;
        var path = Path.Combine(Path.GetTempPath(), $"repartocopier-direct-qd-{Guid.NewGuid():N}.bin");
        try
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                FileOptions.SequentialScan);
            RandomAccess.SetLength(handle, (long)blockBytes * blockCount);
            using var session = new DirectIoDestinationWriter.Session(handle, 4096);
            using var scheduler = new DeviceScheduler("PhysicalDiskConcurrent", 8, 64L * 1024 * 1024);
            var leases = Enumerable.Range(0, blockCount)
                .Select(_ => SourceBufferLease.RentAligned(blockBytes, 64 * 1024))
                .ToArray();
            try
            {
                for (var index = 0; index < leases.Length; index++)
                    leases[index].Memory.Span.Fill(checked((byte)(index + 1)));

                var writes = leases.Select((lease, index) => session.WriteAsync(
                    lease.Memory,
                    (long)index * blockBytes,
                    (long)blockBytes * blockCount,
                    lease.IsAlignedFor(4096),
                    scheduler,
                    CancellationToken.None)).ToArray();
                await Task.WhenAll(writes);
                session.FlushToDisk();
            }
            finally
            {
                foreach (var lease in leases)
                    lease.Dispose();
            }

            session.Dispose();
            var actual = await File.ReadAllBytesAsync(path);
            for (var index = 0; index < blockCount; index++)
            {
                var expected = checked((byte)(index + 1));
                Assert.IsTrue(
                    actual.AsSpan(index * blockBytes, blockBytes).IndexOfAnyExcept(expected) < 0,
                    $"El bloque {index} fue escrito fuera de su offset bajo QD concurrente.");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [TestMethod]
    public void FallbackOnlyMasksUnsupportedDirectWriteNotHardwareFaults()
    {
        foreach (var code in new[] { 1, 5, 50, 87 })
            Assert.IsTrue(DirectIoDestinationWriter.IsFallbackable(new DirectIoDestinationWriter.DirectIoWriteException(code, "unsupported")));

        foreach (var code in new[] { 23, 1117 })
            Assert.IsFalse(DirectIoDestinationWriter.IsFallbackable(new DirectIoDestinationWriter.DirectIoWriteException(code, "hardware fault")));
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media) =>
        new(
            @"E:\copy",
            @"E:\",
            4,
            1,
            bus,
            media,
            false,
            512,
            4096,
            true,
            null,
            false,
            "NTFS",
            DriveType.Fixed,
            false,
            true,
            true,
            0);
}
