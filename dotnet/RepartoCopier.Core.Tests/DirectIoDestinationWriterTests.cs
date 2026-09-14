using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DirectIoDestinationWriterTests
{
    [TestMethod]
    public void LocalHddAndSsdAreEligibleWithoutFixedFileSizeFloor()
    {
        var hdd = Device("SATA", StorageMediaKind.Rotational);
        var ssd = Device("NVMe", StorageMediaKind.SolidState);

        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(hdd, 64L * 1024 * 1024));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 64L * 1024 * 1024));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 4096));
        Assert.IsTrue(DirectIoDestinationWriter.IsEligible(ssd, 1));
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
                FileOptions.Asynchronous);
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
